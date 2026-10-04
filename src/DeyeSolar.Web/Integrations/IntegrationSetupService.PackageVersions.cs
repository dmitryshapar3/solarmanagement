using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SolarManagement.Integrations.Contracts;

namespace DeyeSolar.Web.Integrations;

public sealed partial class IntegrationSetupService
{
    public async Task<IntegrationInstanceDto> SwitchPackageAsync(Guid id, IntegrationPackageChange request,
        System.Security.Claims.ClaimsPrincipal actor, CancellationToken ct)
    {
        await EnsureManagerAsync(actor, ct);
        if (request.Guard is null || string.IsNullOrWhiteSpace(request.TargetPackageVersion))
            throw new IntegrationRequestException("validation", "Choose an installed provider version.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var instance = await FindAsync(db, id, ct);
        Guard(instance, request.Guard.ExpectedRevision, request.Guard.PackageVersion, request.Guard.PackageDigest, request.Guard.DescriptorDigest);
        var target = await catalog.GetAsync(instance.ProviderId, request.TargetPackageVersion, ct);
        ValidateDescriptor(target);
        if (target.PackageDigest == instance.PackageDigest) return instance.ToDto();
        if (target.ConfigurationVersion != instance.ConfigurationVersion)
            throw new IntegrationRequestException("configuration_migration_required", "Create and verify a new connection for this configuration version before selecting its devices.", 409);
        var saved = await ConfigurationAsync(db, instance, ct);
        var fields = target.Fields.ToDictionary(field => field.Key, StringComparer.Ordinal);
        var values = ReadValues(saved).Where(value => fields.TryGetValue(value.Key, out var field) && !field.Secret && field.Kind != "secret")
            .ToDictionary(value => value.Key, value => value.Value.Clone(), StringComparer.Ordinal);
        foreach (var field in target.Fields.Where(field => !field.Secret && field.Kind != "secret" && field.Required && !values.ContainsKey(field.Key)))
            if (field.DefaultValue is { } value) values[field.Key] = value.Clone();
        var previousSecrets = Open(instance, saved);
        var allowedSecrets = previousSecrets.Where(value => fields.TryGetValue(value.Key, out var field) && (field.Secret || field.Kind == "secret"))
            .ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);
        var targetSaved = new IntegrationConfigurationEntity
        {
            ValuesJson = JsonSerializer.Serialize(values, IntegrationJson.Options),
            Revision = saved.Revision,
            SecretsCiphertext = secrets.Encrypt(instance.InstallationId, id, saved.Revision, allowedSecrets)
        };
        var draft = new IntegrationConfigurationChange(instance.Revision, instance.PackageVersion, instance.PackageDigest,
            instance.DescriptorDigest, values, new());
        var resolved = Resolve(instance, targetSaved, target, draft);
        var original = new IntegrationDraftConfiguration(IntegrationJson.Element(ReadValues(saved)), previousSecrets);
        var sameConfiguration = Fingerprint(original) == Fingerprint(resolved);
        var candidate = new IntegrationInstanceEntity { ProviderId = instance.ProviderId, PackageVersion = target.PackageVersion, PackageDigest = target.PackageDigest };
        var tested = await RunTestAsync(candidate, resolved, ct);
        if (!tested.Success) throw new IntegrationRequestException("connection_test_failed", "The target provider version could not verify this connection.", 409);
        if (instance.AccountIdentity is not null && tested.AccountIdentity != instance.AccountIdentity
            && await db.IntegrationDeviceBindings.AnyAsync(binding => binding.InstanceId == id, ct))
            throw new IntegrationRequestException("account_reverification_required", "The target provider version could not verify the saved account identity. Create and verify a new connection.", 409);
        if (!sameConfiguration && await db.IntegrationDeviceBindings.AnyAsync(binding => binding.InstanceId == id, ct)
            && (tested.AccountIdentity is null || tested.AccountIdentity != instance.AccountIdentity))
            throw new IntegrationRequestException("account_reverification_required", "Create a new connection and select its devices because account continuity could not be verified.", 409);
        await EnsureManagerAsync(actor, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockSettingsMutationAsync(db, instance, ct);
        await EnsureNoActiveCommandsAsync(db, id, ct);
        instance.PackageVersion = target.PackageVersion;
        instance.PackageDigest = target.PackageDigest;
        instance.DescriptorDigest = target.DescriptorDigest;
        instance.AccountIdentity = tested.AccountIdentity ?? instance.AccountIdentity;
        configurationWriter.AppendRevision(db, instance, resolved);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw Conflict(); }
        await transaction.CommitAsync(ct);
        changes.Publish(instance.InstallationId, id);
        return instance.ToDto();
    }
}
