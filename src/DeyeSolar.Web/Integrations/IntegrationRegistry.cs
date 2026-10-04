using System.Text.Json;
using DeyeSolar.Web.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SolarManagement.Integrations.Contracts;

namespace DeyeSolar.Web.Integrations;

public sealed record IntegrationRegistrySnapshot(IntegrationInstanceDto Instance, JsonElement Values, string? AccountIdentity);

public interface IIntegrationRegistry
{
    Task<IReadOnlyList<IntegrationDeviceBindingEntity>> ListBindingsAsync(CancellationToken ct);
    Task<IntegrationDeviceBindingEntity?> FindBindingAsync(Guid deviceId, CancellationToken ct);
    Task<IntegrationDeviceBindingEntity?> GetPrimaryInverterAsync(CancellationToken ct);
    Task<IntegrationRegistrySnapshot?> GetSnapshotAsync(Guid instanceId, CancellationToken ct);
    Task<IntegrationSession> GetRuntimeSessionAsync(Guid instanceId, CancellationToken ct);
}

public sealed class IntegrationSecretStore(IDataProtectionProvider protection)
{
    public string ProtectOperatorOrigins(string value) => protection.CreateProtector("IntegrationApprovedOrigins.v1").Protect(value);
    public string UnprotectOperatorOrigins(string value) => protection.CreateProtector("IntegrationApprovedOrigins.v1").Unprotect(value);
    private IDataProtector OAuthProtector(string installation, Guid flow)
        => protection.CreateProtector("IntegrationOAuthFlow.v1", installation, flow.ToString("D"));
    public string ProtectOAuth(string installation, Guid flow, string value) => OAuthProtector(installation, flow).Protect(value);
    public string UnprotectOAuth(string installation, Guid flow, string value)
    {
        try { return OAuthProtector(installation, flow).Unprotect(value); }
        catch (System.Security.Cryptography.CryptographicException)
        { throw new IntegrationRequestException("secret_store_unavailable", "The authorization cannot be opened. Start it again after restoring the installation's encryption keys.", 503); }
    }

    private IDataProtector Protector(string installation, Guid instance, long revision)
        => protection.CreateProtector("IntegrationSecrets.v1", installation, instance.ToString("D"), revision.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public string Encrypt(string installation, Guid instance, long revision, IReadOnlyDictionary<string, string> secrets)
        => Protector(installation, instance, revision).Protect(JsonSerializer.Serialize(secrets, IntegrationJson.Options));

    public Dictionary<string, string> Decrypt(string installation, Guid instance, long revision, string ciphertext)
    {
        if (ciphertext.Length == 0) return new(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(Protector(installation, instance, revision).Unprotect(ciphertext), IntegrationJson.Options)
                ?? throw new InvalidDataException();
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException or InvalidDataException)
        {
            throw new IntegrationRequestException("secret_store_unavailable", "The saved credentials cannot be opened. Restore the installation's encryption keys.", 503);
        }
    }
}

public sealed class IntegrationChangeNotifier(ILogger<IntegrationChangeNotifier> logger)
{
    public event Action<string, Guid>? Changed;
    public void Publish(string installation, Guid instance)
    {
        foreach (var subscriber in Changed?.GetInvocationList() ?? [])
        {
            try { ((Action<string, Guid>)subscriber)(installation, instance); }
            catch (Exception) { logger.LogWarning("An integration change subscriber failed after persistence completed."); }
        }
    }
}

public sealed class IntegrationRegistry(IDbContextFactory<DeyeSolarDbContext> factory, IntegrationSecretStore secrets) : IIntegrationRegistry
{
    public async Task<IReadOnlyList<IntegrationDeviceBindingEntity>> ListBindingsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Set<IntegrationDeviceBindingEntity>().AsNoTracking().OrderBy(b => b.Name).ThenBy(b => b.Id).ToListAsync(ct);
    }
    public async Task<IntegrationDeviceBindingEntity?> FindBindingAsync(Guid deviceId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Set<IntegrationDeviceBindingEntity>().AsNoTracking().SingleOrDefaultAsync(b => b.Id == deviceId, ct);
    }
    public async Task<IntegrationDeviceBindingEntity?> GetPrimaryInverterAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await (from binding in db.Set<IntegrationDeviceBindingEntity>().AsNoTracking()
                      join instance in db.Set<IntegrationInstanceEntity>() on binding.InstanceId equals instance.Id
                      where binding.Kind == "inverter" && binding.IsDefault && binding.Enabled && instance.State == "enabled"
                      select binding).SingleOrDefaultAsync(ct);
    }
    public async Task<IntegrationRegistrySnapshot?> GetSnapshotAsync(Guid instanceId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var instance = await db.Set<IntegrationInstanceEntity>().AsNoTracking().SingleOrDefaultAsync(i => i.Id == instanceId, ct);
        if (instance is null) return null;
        var config = await db.Set<IntegrationConfigurationEntity>().AsNoTracking().SingleAsync(c => c.InstanceId == instanceId && c.Revision == instance.Revision, ct);
        return new(instance.ToDto(), JsonDocument.Parse(config.ValuesJson).RootElement.Clone(), instance.AccountIdentity);
    }
    public async Task<IntegrationSession> GetRuntimeSessionAsync(Guid instanceId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var instance = await db.Set<IntegrationInstanceEntity>().AsNoTracking().SingleOrDefaultAsync(i => i.Id == instanceId, ct)
            ?? throw new IntegrationRequestException("integration_not_found", "This integration is not available in this installation.", 404);
        if (instance.State != "enabled") throw new IntegrationRequestException("integration_disabled", "This integration is disabled.", 409);
        var config = await db.Set<IntegrationConfigurationEntity>().AsNoTracking().SingleAsync(c => c.InstanceId == instanceId && c.Revision == instance.Revision, ct);
        return new(instance.InstallationId, instance.Id, new(instance.ProviderId, instance.PackageVersion, instance.PackageDigest), instance.Revision,
            instance.Generation, new(JsonDocument.Parse(config.ValuesJson).RootElement.Clone(), secrets.Decrypt(instance.InstallationId, instance.Id, config.Revision, config.SecretsCiphertext)));
    }
}
