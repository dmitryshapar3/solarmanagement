using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Billing;
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
    TimeProvider clock, CurrentInstallation current, IntegrationChangeNotifier changes, IntegrationSetupGate gate,
    IIntegrationManagerAccess managerAccess, IIntegrationConfigurationResolver configurationResolver,
    IIntegrationSelectionTokens selectionTokens, IIntegrationDeviceBindingWriter bindingWriter,
    IIntegrationConfigurationWriter configurationWriter, IIntegrationConnectionLifecycle lifecycle,
    IOptions<IntegrationRuntimeOptions>? runtimeOptions = null, IntegrationOAuthService? oauth = null,
    IBillingAccessReader? billing = null, ITrialSocketQuota? quota = null)
{
    private int SetupTimeoutSeconds => runtimeOptions?.Value.MaximumNegotiatedRequestTimeoutSeconds ?? 300;

    private Task EnsureManagerAsync(ClaimsPrincipal actor, CancellationToken ct)
        => managerAccess.EnsureAsync(actor, current.Id ?? throw new IntegrationRequestException("forbidden", "No installation is available.", 403), ct);
    private static IntegrationRequestException Conflict() => new("configuration_conflict", "The integration changed. Reload its settings before continuing.", 409);
    private static void Guard(IntegrationInstanceEntity instance, long revision, string version, string digest, string descriptor)
        => IntegrationConfigurationIdentity.Guard(instance, revision, version, digest, descriptor);
    private static void Guard(IntegrationInstanceEntity instance, IntegrationConfigurationChange change)
        => Guard(instance, change.ExpectedRevision, change.PackageVersion, change.PackageDigest, change.DescriptorDigest);
    private static void ValidateDescriptor(IntegrationProviderDescriptor descriptor)
    {
        try { IntegrationDescriptorValidator.Validate(descriptor); }
        catch (InvalidDataException) { throw new IntegrationRequestException("unsupported_ui", "This provider requires a newer or corrected settings interface.", 409); }
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
        await InstallationMutationGuard.EnsureEnabledAsync(db, ct);
        // Command intent insertion locks this same parent. Keep the lock until settings commit,
        // so an accepted active command cannot be retired between the check and generation change.
        var locked = await IntegrationPersistenceGuard.LockInstanceAsync(db, expected.Id, ct) ?? throw Conflict();
        Guard(locked, expected.Revision, expected.PackageVersion, expected.PackageDigest, expected.DescriptorDigest);
        if (locked.Generation != expected.Generation) throw Conflict();
    }
    private Dictionary<string, string> Open(IntegrationInstanceEntity instance, IntegrationConfigurationEntity config)
        => configurationResolver.ReadSecrets(instance, config);
    private static Dictionary<string, JsonElement> ReadValues(IntegrationConfigurationEntity config)
        => IntegrationConfigurationResolver.ReadValues(config);

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
        await EnsureManagerAsync(actor, ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await InstallationMutationGuard.EnsureEnabledAsync(db, ct);
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
        await transaction.CommitAsync(ct);
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
        IntegrationProviderDescriptor descriptor, IntegrationConfigurationChange draft, bool allowMissingOAuthSecrets = false)
        => configurationResolver.Resolve(instance, saved, descriptor, draft, allowMissingOAuthSecrets);
    private static string Fingerprint(IntegrationDraftConfiguration value) => IntegrationConfigurationResolver.Fingerprint(value);
    private static ProviderPackageIdentity Package(IntegrationInstanceEntity value) => new(value.ProviderId, value.PackageVersion, value.PackageDigest);
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
        var resolved = await ResolveDraftAsync(instance, await ConfigurationAsync(db, instance, ct), descriptor, draft, actor, ct);
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
        var resolved = await ResolveDraftAsync(instance, saved, descriptor, draft, actor, ct);
        var existing = new IntegrationDraftConfiguration(IntegrationJson.Element(ReadValues(saved)), Open(instance, saved));
        if (draft.OAuthFlowId is null && Fingerprint(existing) == Fingerprint(resolved)) return await ReadAsync(id, ct);
        if (instance.AccountIdentity is not null || await db.Set<IntegrationDeviceBindingEntity>().AnyAsync(b => b.InstanceId == id, ct))
        {
            if (instance.State == "enabled") throw new IntegrationRequestException("disable_before_edit", "Disable this integration before changing its connection settings.", 409);
            var verification = await RunTestAsync(instance, resolved, ct);
            if (!verification.Success || string.IsNullOrEmpty(instance.AccountIdentity) || verification.AccountIdentity != instance.AccountIdentity)
                throw new IntegrationRequestException("account_replacement_requires_new_instance", "Create a new integration for changed account credentials. Existing devices and history will remain linked to this account.", 409);
        }
        await EnsureManagerAsync(actor, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockSettingsMutationAsync(db, instance, ct);
        await EnsureNoActiveCommandsAsync(db, id, ct);
        if (draft.OAuthFlowId is { } authorization)
            await (oauth ?? throw new IntegrationRequestException("authorization_unavailable", "Authorization is unavailable.", 503))
                .ConsumeAsync(db, instance, authorization, actor, ct);
        configurationWriter.AppendRevision(db, instance, resolved);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw Conflict(); }
        await transaction.CommitAsync(ct);
        changes.Publish(instance.InstallationId, id);
        return await ReadAsync(id, ct);
    }
    public async Task<IntegrationDiscoveryResponse> DiscoverAsync(Guid id, IntegrationConfigurationChange draft, ClaimsPrincipal actor, CancellationToken ct)
    {
        await EnsureManagerAsync(actor, ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var instance = await FindAsync(db, id, ct);
        var descriptor = await DescriptorAsync(instance, ct);
        if (!descriptor.Actions.Contains("discover", StringComparer.Ordinal)) throw new IntegrationRequestException("unsupported_action", "This provider does not support device discovery.");
        var resolved = await ResolveDraftAsync(instance, await ConfigurationAsync(db, instance, ct), descriptor, draft, actor, ct);
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
        return selectionTokens.Issue(instance, result, fingerprint, expiry);
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
        if (!DeyeSolar.Web.Services.DeviceNameService.TryName(request.DisplayName, out var displayName))
            throw new IntegrationRequestException("validation", "Use a device name of up to 80 characters without control characters.");
        var proof = selectionTokens.Read(request.SelectionToken);
        await using var db = await factory.CreateDbContextAsync(ct);
        var instance = await FindAsync(db, id, ct);
        var descriptor = await DescriptorAsync(instance, ct);
        var config = await ConfigurationAsync(db, instance, ct);
        var resolved = await ResolveDraftAsync(instance, config, descriptor, request.Draft, actor, ct);
        Guard(instance, proof.Revision, proof.PackageVersion, proof.PackageDigest, proof.DescriptorDigest);
        if (proof.InstallationId != instance.InstallationId || proof.InstanceId != id || proof.ExpiresAt <= clock.GetUtcNow() || proof.Fingerprint != Fingerprint(resolved))
            throw new IntegrationRequestException("invalid_selection", "Discover devices again using this integration's current settings.", 409);
        if (proof.Fingerprint != Fingerprint(new(IntegrationJson.Element(ReadValues(config)), Open(instance, config))))
            throw new IntegrationRequestException("save_before_selection", "Save the settings, then discover devices again before selecting a device.", 409);
        var device = proof.Device;
        var channel = device.Channel ?? "";
        await EnsureManagerAsync(actor, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await InstallationMutationGuard.EnsureEnabledAsync(db, ct);
        if (device.Kind == "socket" && billing is not null)
            await (quota ?? throw new InvalidOperationException("A billing-enabled integration service requires a socket quota policy.")).LockSocketSelectionAccountAsync(db, actor, instance.InstallationId, ct);
        var locked = await IntegrationPersistenceGuard.LockInstanceAsync(db, id, ct) ?? throw Conflict();
        db.Entry(instance).CurrentValues.SetValues(locked);
        db.Entry(instance).OriginalValues.SetValues(locked);
        Guard(instance, proof.Revision, proof.PackageVersion, proof.PackageDigest, proof.DescriptorDigest);
        await EnsureNoActiveCommandsAsync(db, id, ct);
        if (instance.AccountIdentity is not null && instance.AccountIdentity != proof.Device.AccountIdentity)
            throw new IntegrationRequestException("account_identity_changed", "The discovered device belongs to a different account. Create a new integration.", 409);
        if (device.Kind == "socket" && billing is not null)
            await (quota ?? throw new InvalidOperationException("A billing-enabled integration service requires a socket quota policy.")).EnsureSocketSelectionAsync(db, actor, instance.InstallationId, new(id, device.RemoteId, channel), ct);
        var binding = await bindingWriter.BindAsync(db, instance, device, actor, ct);
        if (request.DisplayName is not null)
            binding.MetadataJson = IntegrationDeviceDisplayName.Write(binding, displayName);
        instance.AccountIdentity ??= device.AccountIdentity;
        lifecycle.Touch(instance);
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
        await EnsureManagerAsync(actor, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockSettingsMutationAsync(db, instance, ct);
        if (enabled) await EnsureNoActiveCommandsAsync(db, id, ct);
        var changed = await lifecycle.SetEnabledAsync(db, instance, enabled, ct);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw Conflict(); }
        await transaction.CommitAsync(ct);
        if (changed) changes.Publish(instance.InstallationId, id);
        return instance.ToDto();
    }
}
