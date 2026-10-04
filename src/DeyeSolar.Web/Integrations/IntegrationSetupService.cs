using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeyeSolar.Web.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SolarManagement.Integrations.Contracts;
using SolarManagement.Integrations.Runtime;

namespace DeyeSolar.Web.Integrations;

public sealed class IntegrationSetupGate
{
    private readonly SemaphoreSlim _slots = new(4, 4);
    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        if (!await _slots.WaitAsync(0, ct)) throw new IntegrationRequestException("busy", "Connection checks are busy. Please try again shortly.", 429);
        return new Lease(_slots);
    }
    private sealed class Lease(SemaphoreSlim slots) : IDisposable
    {
        private SemaphoreSlim? _slots = slots;
        public void Dispose() => Interlocked.Exchange(ref _slots, null)?.Release();
    }
}

public sealed partial class IntegrationSetupService(IDbContextFactory<DeyeSolarDbContext> factory,
    IIntegrationProviderCatalog catalog, IIntegrationSetupExecutor executor, IntegrationSecretStore secrets,
    IDataProtectionProvider protection, TimeProvider clock, InstallationMembershipService memberships,
    CurrentInstallation current, IntegrationChangeNotifier changes, IntegrationSetupGate gate,
    IOptions<IntegrationRuntimeOptions>? runtimeOptions = null)
{
    private static readonly HashSet<string> SupportedFields = new(["text", "secret", "integer", "number", "boolean", "select"], StringComparer.Ordinal);
    private int SetupTimeoutSeconds => runtimeOptions?.Value.MaximumNegotiatedRequestTimeoutSeconds ?? 300;

    private async Task EnsureManagerAsync(ClaimsPrincipal actor, CancellationToken ct)
    {
        var membership = await memberships.ResolveAsync(actor, ct);
        if (membership is null || membership.InstallationId != current.Id || membership.Role is not ("Owner" or "IntegrationManager"))
            throw new IntegrationRequestException("forbidden", "You do not have permission to manage this installation's integrations.", 403);
    }
    private static IntegrationRequestException Conflict() => new("configuration_conflict", "The integration changed. Reload its settings before continuing.", 409);
    private static void Guard(IntegrationInstanceEntity instance, long revision, string version, string digest, string descriptor)
    {
        if (instance.Revision != revision || instance.PackageVersion != version || instance.PackageDigest != digest || instance.DescriptorDigest != descriptor) throw Conflict();
    }
    private static void Guard(IntegrationInstanceEntity instance, IntegrationConfigurationChange change)
        => Guard(instance, change.ExpectedRevision, change.PackageVersion, change.PackageDigest, change.DescriptorDigest);
    private static void ValidateDescriptor(IntegrationProviderDescriptor descriptor)
    {
        if (descriptor.UiContractVersion != 1 || descriptor.RequiredUiFeatures.Any(f => !SupportedFields.Contains(f))
            || descriptor.Fields.Count > 100 || descriptor.Fields.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count() != descriptor.Fields.Count
            || descriptor.Fields.Any(f => !SupportedFields.Contains(f.Kind) || string.IsNullOrWhiteSpace(f.Key) || f.Key.Length > 64))
            throw new IntegrationRequestException("unsupported_ui", "This provider requires a newer settings interface.", 409);
    }
    private async Task<IntegrationProviderDescriptor> DescriptorAsync(IntegrationInstanceEntity instance, CancellationToken ct)
    {
        var descriptor = await catalog.GetAsync(instance.ProviderId, instance.PackageVersion, ct);
        ValidateDescriptor(descriptor);
        if (descriptor.PackageDigest != instance.PackageDigest || descriptor.DescriptorDigest != instance.DescriptorDigest
            || descriptor.ConfigurationVersion != instance.ConfigurationVersion) throw Conflict();
        return descriptor;
    }
    private static async Task<IntegrationInstanceEntity> FindAsync(DeyeSolarDbContext db, Guid id, CancellationToken ct)
        => await db.Set<IntegrationInstanceEntity>().SingleOrDefaultAsync(i => i.Id == id, ct)
            ?? throw new IntegrationRequestException("integration_not_found", "This integration is not available in this installation.", 404);
    private static async Task<IntegrationConfigurationEntity> ConfigurationAsync(DeyeSolarDbContext db, IntegrationInstanceEntity instance, CancellationToken ct)
        => await db.Set<IntegrationConfigurationEntity>().SingleAsync(c => c.InstanceId == instance.Id && c.Revision == instance.Revision, ct);
    private static async Task EnsureNoActiveCommandsAsync(DeyeSolarDbContext db, Guid instanceId, CancellationToken ct)
    {
        if (await db.IntegrationCommands.AnyAsync(command => command.InstanceId == instanceId && (command.Status == "requested" || command.Status == "pending"), ct))
            throw new IntegrationRequestException("active_commands", "Wait for or check active device commands before changing this integration.", 409);
    }
    private static async Task LockSettingsMutationAsync(DeyeSolarDbContext db, IntegrationInstanceEntity expected, CancellationToken ct)
    {
        // Command intent insertion locks this same parent. Keep the lock until settings commit,
        // so an accepted active command cannot be retired between the check and generation change.
        var locked = await IntegrationPersistenceGuard.LockInstanceAsync(db, expected.Id, ct) ?? throw Conflict();
        Guard(locked, expected.Revision, expected.PackageVersion, expected.PackageDigest, expected.DescriptorDigest);
        if (locked.Generation != expected.Generation) throw Conflict();
    }
    private Dictionary<string, string> Open(IntegrationInstanceEntity instance, IntegrationConfigurationEntity config)
        => secrets.Decrypt(instance.InstallationId, instance.Id, config.Revision, config.SecretsCiphertext);
    private static Dictionary<string, JsonElement> ReadValues(IntegrationConfigurationEntity config)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(config.ValuesJson, IntegrationJson.Options) ?? new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<IntegrationInstanceDto>> ListAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return (await db.Set<IntegrationInstanceEntity>().AsNoTracking().OrderBy(i => i.Name).ToListAsync(ct)).Select(i => i.ToDto()).ToList();
    }
    public async Task<IntegrationInstanceDto> CreateAsync(CreateIntegrationRequest request, ClaimsPrincipal actor, CancellationToken ct)
    {
        await EnsureManagerAsync(actor, ct);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 128 || request.Name.Any(char.IsControl))
            throw new IntegrationRequestException("validation", "Enter an integration name of up to 128 characters.");
        var descriptor = await catalog.GetAsync(request.ProviderId, null, ct);
        ValidateDescriptor(descriptor);
        await using var db = await factory.CreateDbContextAsync(ct);
        var instance = new IntegrationInstanceEntity
        {
            Id = Guid.NewGuid(),
            InstallationId = current.Id!,
            ProviderId = descriptor.ProviderId,
            Name = request.Name.Trim(),
            PackageVersion = descriptor.PackageVersion,
            PackageDigest = descriptor.PackageDigest,
            DescriptorDigest = descriptor.DescriptorDigest,
            ConfigurationVersion = descriptor.ConfigurationVersion,
            CreatedAt = clock.GetUtcNow(),
            UpdatedAt = clock.GetUtcNow()
        };
        var defaults = descriptor.Fields.Where(f => !f.Secret && f.Kind != "secret" && f.DefaultValue.HasValue).ToDictionary(f => f.Key, f => f.DefaultValue!.Value.Clone(), StringComparer.Ordinal);
        db.Add(instance);
        db.Add(new IntegrationConfigurationEntity
        {
            InstallationId = instance.InstallationId,
            InstanceId = instance.Id,
            Revision = instance.Revision,
            ValuesJson = JsonSerializer.Serialize(defaults, IntegrationJson.Options),
            SecretsCiphertext = secrets.Encrypt(instance.InstallationId, instance.Id, 1, new Dictionary<string, string>()),
            CreatedAt = clock.GetUtcNow()
        });
        await db.SaveChangesAsync(ct);
        changes.Publish(instance.InstallationId, instance.Id);
        return instance.ToDto();
    }
    public async Task<IntegrationConfigurationDto> ReadAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var instance = await FindAsync(db, id, ct);
        var config = await ConfigurationAsync(db, instance, ct);
        var saved = Open(instance, config);
        return new(instance.ToDto(), ReadValues(config), saved.Keys.ToDictionary(key => key, _ => true, StringComparer.Ordinal));
    }
    private IntegrationDraftConfiguration Resolve(IntegrationInstanceEntity instance, IntegrationConfigurationEntity saved,
        IntegrationProviderDescriptor descriptor, IntegrationConfigurationChange draft)
    {
        Guard(instance, draft);
        if (draft.Values is null || draft.SecretOperations is null || draft.Values.Count > 100 || draft.SecretOperations.Count > 100)
            throw new IntegrationRequestException("validation", "The configuration fields are invalid.");
        var fields = descriptor.Fields.ToDictionary(f => f.Key, StringComparer.Ordinal);
        if (draft.Values.Keys.Any(k => !fields.TryGetValue(k, out var f) || f.Secret || f.Kind == "secret")
            || draft.SecretOperations.Keys.Any(k => !fields.TryGetValue(k, out var f) || !f.Secret && f.Kind != "secret"))
            throw new IntegrationRequestException("validation", "The configuration contains an unknown field.");
        var resolvedSecrets = Open(instance, saved);
        foreach (var (key, operation) in draft.SecretOperations)
        {
            if (operation is null) throw new IntegrationRequestException("validation", "Choose a credential operation.");
            switch (operation.Operation)
            {
                case "keep": break;
                case "clear": resolvedSecrets.Remove(key); break;
                case "replace" when operation.Value is { Length: > 0 and <= 8192 }: resolvedSecrets[key] = operation.Value; break;
                default: throw new IntegrationRequestException("validation", "Enter a credential or choose keep or clear.");
            }
        }
        foreach (var field in descriptor.Fields)
        {
            if (field.Secret || field.Kind == "secret")
            {
                if (field.Required && (!resolvedSecrets.TryGetValue(field.Key, out var value) || string.IsNullOrWhiteSpace(value)))
                    throw new IntegrationRequestException("validation", $"Enter {field.Label}.");
                continue;
            }
            if (!draft.Values.TryGetValue(field.Key, out var json) || json.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                if (field.Required) throw new IntegrationRequestException("validation", $"Enter {field.Label}.");
                continue;
            }
            var valid = field.Kind switch
            {
                "text" => json.ValueKind == JsonValueKind.String && json.GetString()!.Length <= 2048 && (!field.Required || !string.IsNullOrWhiteSpace(json.GetString())),
                "select" => json.ValueKind == JsonValueKind.String && field.Options?.Any(o => o.Value == json.GetString()) == true,
                "boolean" => json.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "integer" or "number" => json.ValueKind == JsonValueKind.Number && json.TryGetDecimal(out var number)
                    && (field.Kind != "integer" || decimal.Truncate(number) == number) && (!field.Minimum.HasValue || number >= field.Minimum)
                    && (!field.Maximum.HasValue || number <= field.Maximum),
                _ => false
            };
            if (!valid) throw new IntegrationRequestException("validation", $"Check {field.Label}.");
        }
        return new(IntegrationJson.Element(draft.Values), resolvedSecrets);
    }
    private static string Fingerprint(IntegrationDraftConfiguration value)
    {
        var orderedValues = value.Values.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        var orderedSecrets = value.Secrets.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { values = orderedValues, secrets = orderedSecrets }, IntegrationJson.Options))));
    }
    private static ProviderPackageIdentity Package(IntegrationInstanceEntity value) => new(value.ProviderId, value.PackageVersion, value.PackageDigest);
    private static IntegrationDiscoveredDevice PublicDevice(IntegrationDiscoveredDevice device)
    {
        var capabilities = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (device.Metadata is { ValueKind: JsonValueKind.Object } metadata
            && metadata.TryGetProperty("capabilities", out var source) && source.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "hasBattery", "hasSolarPower", "hasSignedGridPower", "hasLoadPower", "hasGridPowerHistory", "canSwitch", "canMeasurePower" })
                if (source.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    capabilities[key] = value.Clone();
            if (source.TryGetProperty("solarBasis", out var basis) && basis.ValueKind == JsonValueKind.String
                && basis.GetString() is "Unknown" or "PvDc" or "InverterAcOutput") capabilities["solarBasis"] = basis.Clone();
        }
        // Workers return only public capability data across the discovery boundary.
        return device with { Metadata = IntegrationJson.Element(new { capabilities }) };
    }
    private async Task<IntegrationTestResult> RunTestAsync(IntegrationInstanceEntity instance, IntegrationDraftConfiguration draft, CancellationToken ct)
    {
        using var lease = await gate.EnterAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(SetupTimeoutSeconds));
        try { return await executor.TestAsync(Package(instance), draft, timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, "timeout", "The provider did not respond in time. Please try again."); }
        catch (Exception) when (!ct.IsCancellationRequested) { return new(false, "unavailable", "The provider could not be verified. Check the configuration or try again later."); }
    }
    public async Task<IntegrationTestResult> TestAsync(Guid id, IntegrationConfigurationChange draft, ClaimsPrincipal actor, CancellationToken ct)
    {
        await EnsureManagerAsync(actor, ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var instance = await FindAsync(db, id, ct);
        var descriptor = await DescriptorAsync(instance, ct);
        if (!descriptor.Actions.Contains("test", StringComparer.Ordinal)) throw new IntegrationRequestException("unsupported_action", "This provider does not support connection checks.");
        var resolved = Resolve(instance, await ConfigurationAsync(db, instance, ct), descriptor, draft);
        var result = await RunTestAsync(instance, resolved, ct);
        return result with
        {
            AccountIdentity = null,
            Code = result.Success ? "ok" : "unavailable",
            Message = result.Success ? "Connection verified." : "The provider could not be verified. Check the configuration or try again later."
        };
    }
    public async Task<IntegrationConfigurationDto> SaveAsync(Guid id, IntegrationConfigurationChange draft, ClaimsPrincipal actor, CancellationToken ct)
    {
        await EnsureManagerAsync(actor, ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var instance = await FindAsync(db, id, ct);
        var descriptor = await DescriptorAsync(instance, ct);
        var saved = await ConfigurationAsync(db, instance, ct);
        var resolved = Resolve(instance, saved, descriptor, draft);
        var existing = new IntegrationDraftConfiguration(IntegrationJson.Element(ReadValues(saved)), Open(instance, saved));
        if (Fingerprint(existing) == Fingerprint(resolved)) return await ReadAsync(id, ct);
        if (await db.Set<IntegrationDeviceBindingEntity>().AnyAsync(b => b.InstanceId == id, ct))
        {
            if (instance.State == "enabled") throw new IntegrationRequestException("disable_before_edit", "Disable this integration before changing its connection settings.", 409);
            var verification = await RunTestAsync(instance, resolved, ct);
            if (!verification.Success || string.IsNullOrEmpty(instance.AccountIdentity) || verification.AccountIdentity != instance.AccountIdentity)
                throw new IntegrationRequestException("account_replacement_requires_new_instance", "Create a new integration for changed account credentials. Existing devices and history will remain linked to this account.", 409);
        }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockSettingsMutationAsync(db, instance, ct);
        await EnsureNoActiveCommandsAsync(db, id, ct);
        instance.Revision++;
        instance.Generation++;
        instance.UpdatedAt = clock.GetUtcNow();
        db.Add(new IntegrationConfigurationEntity
        {
            InstallationId = instance.InstallationId,
            InstanceId = id,
            Revision = instance.Revision,
            ValuesJson = resolved.Values.GetRawText(),
            SecretsCiphertext = secrets.Encrypt(instance.InstallationId, id, instance.Revision, resolved.Secrets),
            CreatedAt = clock.GetUtcNow()
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw Conflict(); }
        await transaction.CommitAsync(ct);
        changes.Publish(instance.InstallationId, id);
        return await ReadAsync(id, ct);
    }
    private sealed record SelectionProof(string InstallationId, Guid InstanceId, long Revision, string PackageVersion,
        string PackageDigest, string DescriptorDigest, string Fingerprint, DateTimeOffset ExpiresAt, IntegrationDiscoveredDevice Device);
    private IDataProtector SelectionProtector => protection.CreateProtector("IntegrationDiscoverySelection.v1");
    public async Task<IntegrationDiscoveryResponse> DiscoverAsync(Guid id, IntegrationConfigurationChange draft, ClaimsPrincipal actor, CancellationToken ct)
    {
        await EnsureManagerAsync(actor, ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var instance = await FindAsync(db, id, ct);
        var descriptor = await DescriptorAsync(instance, ct);
        if (!descriptor.Actions.Contains("discover", StringComparer.Ordinal)) throw new IntegrationRequestException("unsupported_action", "This provider does not support device discovery.");
        var resolved = Resolve(instance, await ConfigurationAsync(db, instance, ct), descriptor, draft);
        using var lease = await gate.EnterAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(SetupTimeoutSeconds));
        IReadOnlyList<IntegrationDiscoveredDevice> result;
        try { result = await executor.DiscoverAsync(Package(instance), resolved, new(), timeout.Token); }
        catch (Exception) when (!ct.IsCancellationRequested) { throw new IntegrationRequestException("unavailable", "Devices could not be discovered. Check the configuration and try again.", 503); }
        if (result.Count > 1000 || result.Any(d => string.IsNullOrWhiteSpace(d.RemoteId) || d.RemoteId.Length > 256 || d.Channel?.Length > 128
            || d.Name.Length > 128 || d.Kind is not ("inverter" or "socket") || d.AccountIdentity?.Length > 256))
            throw new IntegrationRequestException("invalid_provider_response", "The provider returned unsupported device information.", 503);
        var expiry = clock.GetUtcNow().AddMinutes(5);
        var fingerprint = Fingerprint(resolved);
        return new(result.Select(PublicDevice).Select(device => new IntegrationDiscoveryDevice(SelectionProtector.Protect(JsonSerializer.Serialize(
            new SelectionProof(instance.InstallationId, id, instance.Revision, instance.PackageVersion, instance.PackageDigest, instance.DescriptorDigest, fingerprint, expiry, device), IntegrationJson.Options)),
            device.Name, device.Kind, device.RemoteId, device.Channel ?? "", device.Metadata)).ToList(), expiry);
    }
    public async Task<IReadOnlyList<IntegrationBindingDto>> DevicesAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        _ = await FindAsync(db, id, ct);
        return (await db.Set<IntegrationDeviceBindingEntity>().AsNoTracking().Where(b => b.InstanceId == id).ToListAsync(ct)).Select(b => b.ToDto()).ToList();
    }
    public async Task<IntegrationBindingDto> SelectDeviceAsync(Guid id, SelectIntegrationDeviceRequest request, ClaimsPrincipal actor, CancellationToken ct)
    {
        await EnsureManagerAsync(actor, ct);
        SelectionProof proof;
        try
        {
            if (string.IsNullOrWhiteSpace(request.SelectionToken) || request.SelectionToken.Length > 32768) throw new InvalidDataException();
            proof = JsonSerializer.Deserialize<SelectionProof>(SelectionProtector.Unprotect(request.SelectionToken), IntegrationJson.Options) ?? throw new InvalidDataException();
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidDataException)
        { throw new IntegrationRequestException("invalid_selection", "Discover devices again before selecting this device."); }
        await using var db = await factory.CreateDbContextAsync(ct);
        var instance = await FindAsync(db, id, ct);
        var descriptor = await DescriptorAsync(instance, ct);
        var config = await ConfigurationAsync(db, instance, ct);
        var resolved = Resolve(instance, config, descriptor, request.Draft);
        Guard(instance, proof.Revision, proof.PackageVersion, proof.PackageDigest, proof.DescriptorDigest);
        if (proof.InstallationId != instance.InstallationId || proof.InstanceId != id || proof.ExpiresAt <= clock.GetUtcNow() || proof.Fingerprint != Fingerprint(resolved))
            throw new IntegrationRequestException("invalid_selection", "Discover devices again using this integration's current settings.", 409);
        if (proof.Fingerprint != Fingerprint(new(IntegrationJson.Element(ReadValues(config)), Open(instance, config))))
            throw new IntegrationRequestException("save_before_selection", "Save the settings, then discover devices again before selecting a device.", 409);
        var device = proof.Device;
        var channel = device.Channel ?? "";
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var locked = await IntegrationPersistenceGuard.LockInstanceAsync(db, id, ct) ?? throw Conflict();
        db.Entry(instance).CurrentValues.SetValues(locked);
        db.Entry(instance).OriginalValues.SetValues(locked);
        Guard(instance, proof.Revision, proof.PackageVersion, proof.PackageDigest, proof.DescriptorDigest);
        await EnsureNoActiveCommandsAsync(db, id, ct);
        if (instance.AccountIdentity is not null && instance.AccountIdentity != proof.Device.AccountIdentity)
            throw new IntegrationRequestException("account_identity_changed", "The discovered device belongs to a different account. Create a new integration.", 409);
        var binding = await db.Set<IntegrationDeviceBindingEntity>().SingleOrDefaultAsync(b => b.InstanceId == id && b.Kind == device.Kind && b.RemoteId == device.RemoteId && b.Channel == channel, ct);
        if (binding is null)
        {
            binding = new()
            {
                Id = Guid.NewGuid(),
                InstallationId = instance.InstallationId,
                InstanceId = id,
                Kind = device.Kind,
                RemoteId = device.RemoteId,
                Channel = channel,
                Name = device.Name,
                AccountIdentity = device.AccountIdentity,
                MetadataJson = device.Metadata?.GetRawText() ?? "{}"
            };
            db.Add(binding);
        }
        if (device.Kind == "inverter")
        {
            // Clear the previous selection inside this transaction before the filtered
            // unique index sees the new binding; readers cannot see a partial cutover.
            await db.Set<IntegrationDeviceBindingEntity>().Where(b => b.Kind == "inverter" && b.IsDefault)
                .ExecuteUpdateAsync(setters => setters.SetProperty(b => b.IsDefault, false), ct);
            binding.IsDefault = true;
            if (db.Entry(binding).State != EntityState.Added)
                db.Entry(binding).Property(b => b.IsDefault).IsModified = true;
        }
        instance.AccountIdentity ??= device.AccountIdentity;
        instance.Generation++;
        instance.UpdatedAt = clock.GetUtcNow();
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw Conflict(); }
        await transaction.CommitAsync(ct);
        changes.Publish(instance.InstallationId, id);
        return binding.ToDto();
    }
    public async Task<IntegrationInstanceDto> SetEnabledAsync(Guid id, bool enabled, IntegrationVersionGuard guard, ClaimsPrincipal actor, CancellationToken ct)
    {
        await EnsureManagerAsync(actor, ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var instance = await FindAsync(db, id, ct);
        Guard(instance, guard.ExpectedRevision, guard.PackageVersion, guard.PackageDigest, guard.DescriptorDigest);
        var desired = enabled ? "enabled" : "disabled";
        var alreadyDesired = instance.State == desired;
        if (alreadyDesired && enabled) return instance.ToDto();
        if (enabled)
        {
            var descriptor = await DescriptorAsync(instance, ct);
            var saved = await ConfigurationAsync(db, instance, ct);
            var draft = new IntegrationConfigurationChange(instance.Revision, instance.PackageVersion, instance.PackageDigest, instance.DescriptorDigest,
                ReadValues(saved), []);
            var resolved = Resolve(instance, saved, descriptor, draft);
            var result = await RunTestAsync(instance, resolved, ct);
            if (!result.Success) throw new IntegrationRequestException("readiness_failed", "The provider could not be verified. Check the configuration or try again later.", 503);
            if (instance.AccountIdentity is not null && result.AccountIdentity is not null && result.AccountIdentity != instance.AccountIdentity)
                throw new IntegrationRequestException("account_identity_changed", "The account identity changed. Create a new integration.", 409);
            instance.AccountIdentity ??= result.AccountIdentity;
        }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockSettingsMutationAsync(db, instance, ct);
        if (enabled) await EnsureNoActiveCommandsAsync(db, id, ct);
        var retired = 0;
        if (!enabled)
            retired = await db.IntegrationCommands.Where(command => command.InstanceId == id && (command.Status == "requested" || command.Status == "pending"))
                .ExecuteUpdateAsync(update => update.SetProperty(command => command.Status, "uncertain")
                    .SetProperty(command => command.ErrorCode, "retired_generation")
                    .SetProperty(command => command.CompletedAt, clock.GetUtcNow()), ct);
        if (!alreadyDesired)
        {
            instance.State = desired;
            instance.Generation++;
            instance.UpdatedAt = clock.GetUtcNow();
        }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw Conflict(); }
        await transaction.CommitAsync(ct);
        if (!alreadyDesired || retired > 0) changes.Publish(instance.InstallationId, id);
        return instance.ToDto();
    }
}
