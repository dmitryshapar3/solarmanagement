using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeyeSolar.Domain.Options;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SolarManagement.Integrations.Contracts;
using SolarManagement.Integrations.Runtime;

namespace DeyeSolar.Web.Integrations;

/// <summary>Imports only the former single-provider connections within one authoritative installation.</summary>
public sealed class LegacyIntegrationBootstrap(IIntegrationProviderCatalog catalog, IntegrationSecretStore secrets,
    TimeProvider clock, IntegrationChangeNotifier changes, IOptions<IntegrationRuntimeOptions>? runtimeOptions = null)
{
    public const string MarkerSection = "IntegrationMigration";
    public const string MarkerKey = "Completed";
    public async Task<bool> RunAsync(IDbContextFactory<DeyeSolarDbContext> factory, IConfiguration effective, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var installation = db.InstallationId ?? throw new InvalidOperationException("Legacy integration migration requires an installation.");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (db.Database.IsSqlServer())
        {
            // Startup, lazy refresh and package installation can request the same cutover concurrently.
            var resource = "legacy-integration-cutover:" + installation;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock @Resource={resource}, @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=15000;
                IF @result < 0 THROW 50000, 'Legacy integration cutover lock was not acquired.', 1;
                """, ct);
        }
        if (await db.AppSettings.AnyAsync(setting => setting.Section == MarkerSection && setting.Key == MarkerKey && setting.Value == "1", ct))
            return true;
        var rows = await db.AppSettings.ToListAsync(ct);
        var deye = new DeyeCloudOptions();
        var shelly = new ShellyOptions();
        effective.GetSection(DeyeCloudOptions.Section).Bind(deye);
        effective.GetSection(ShellyOptions.Section).Bind(shelly);
        ApplyRows(rows, DeyeCloudOptions.Section, deye);
        ApplyRows(rows, ShellyOptions.Section, shelly);
        var hasDeye = !string.IsNullOrWhiteSpace(deye.AppId) || !string.IsNullOrWhiteSpace(deye.AppSecret)
            || !string.IsNullOrWhiteSpace(deye.Email) || !string.IsNullOrWhiteSpace(deye.Password) || !string.IsNullOrWhiteSpace(deye.DeviceSn);
        var hasShelly = !string.IsNullOrWhiteSpace(shelly.ServerUri) || !string.IsNullOrWhiteSpace(shelly.AuthKey) || !string.IsNullOrWhiteSpace(shelly.DeviceId);
        var maximumInterval = ((runtimeOptions?.Value.MaximumNegotiatedRequestTimeoutSeconds ?? 300) - 60) * 500;
        // Do not delete legacy credentials or claim completion when a configured interval cannot be executed.
        if (hasShelly && Math.Max(1000, shelly.RequestIntervalMilliseconds) > maximumInterval) return false;
        var deyePackage = hasDeye ? (await catalog.GetVersionsAsync("deye.cloud", ct)).SingleOrDefault(provider => provider.PackageVersion == "1.0.0") : null;
        var shellyPackage = hasShelly ? (await catalog.GetVersionsAsync("shelly.cloud", ct)).SingleOrDefault(provider => provider.PackageVersion == "1.0.0") : null;
        if (hasDeye && deyePackage is null || hasShelly && shellyPackage is null) return false;
        var now = clock.GetUtcNow();
        var imported = new List<Guid>();
        var aliases = new Dictionary<string, Guid>(StringComparer.Ordinal);
        Guid? inverter = null;
        if (hasDeye)
        {
            var instance = Instance(deyePackage!, installation, now, "Legacy Deye Cloud",
                !string.IsNullOrWhiteSpace(deye.AppId) && !string.IsNullOrWhiteSpace(deye.AppSecret)
                && !string.IsNullOrWhiteSpace(deye.Email) && !string.IsNullOrWhiteSpace(deye.Password) && !string.IsNullOrWhiteSpace(deye.DeviceSn));
            db.IntegrationInstances.Add(instance);
            AddConfiguration(db, instance, IntegrationJson.Element(new { deye.BaseUrl, deye.AppId, deye.Email }),
                new Dictionary<string, string> { ["appSecret"] = deye.AppSecret, ["password"] = deye.Password }, now);
            imported.Add(instance.Id);
            if (!string.IsNullOrWhiteSpace(deye.DeviceSn) && deye.DeviceSn.Length <= 128)
            {
                var binding = Binding(instance, deye.DeviceSn, "", "inverter", "Legacy selected inverter");
                binding.IsDefault = !await db.IntegrationDeviceBindings.AnyAsync(device => device.Kind == "inverter" && device.IsDefault && device.Enabled, ct);
                binding.MetadataJson = IntegrationJson.Element(new
                {
                    capabilities = new
                    {
                        hasBattery = true,
                        hasSolarPower = true,
                        hasSignedGridPower = true,
                        hasLoadPower = true,
                        hasGridPowerHistory = true,
                        solarBasis = "Unknown"
                    }
                }).GetRawText();
                db.IntegrationDeviceBindings.Add(binding);
                inverter = binding.Id;
            }
        }
        var rules = await db.TriggerRules.ToListAsync(ct);
        if (hasShelly)
        {
            var instance = Instance(shellyPackage!, installation, now, "Legacy Shelly Cloud",
                !string.IsNullOrWhiteSpace(shelly.ServerUri) && !string.IsNullOrWhiteSpace(shelly.AuthKey));
            db.IntegrationInstances.Add(instance);
            AddConfiguration(db, instance, IntegrationJson.Element(new
            {
                shelly.ServerUri,
                shelly.DeviceId,
                requestIntervalMilliseconds = Math.Max(1000, shelly.RequestIntervalMilliseconds)
            }),
                new Dictionary<string, string> { ["authKey"] = shelly.AuthKey }, now);
            imported.Add(instance.Id);
            var candidateIds = rules.Select(rule => rule.EntityId).Append(shelly.DeviceId).Where(id => !string.IsNullOrWhiteSpace(id));
            foreach (var original in candidateIds.Distinct(StringComparer.Ordinal))
            {
                var raw = original.StartsWith("shelly:", StringComparison.OrdinalIgnoreCase) ? original[7..] : original;
                if (raw.Length is < 1 or > 128 || raw.Contains(':') || raw != raw.Trim()) continue;
                var binding = Binding(instance, raw, "0", "socket", "Legacy configured socket");
                if (!db.IntegrationDeviceBindings.Local.Any(device => device.Id == binding.Id)) db.IntegrationDeviceBindings.Add(binding);
                aliases[original] = binding.Id;
                aliases.TryAdd(raw, binding.Id);
                aliases.TryAdd("shelly:" + raw, binding.Id);
            }
        }
        foreach (var (legacy, device) in aliases)
            db.IntegrationDeviceAliases.Add(new() { LegacyId = legacy, DeviceId = device });
        foreach (var rule in rules)
        {
            if (aliases.TryGetValue(rule.EntityId, out var target)) rule.EntityId = target.ToString("D");
            else if (!string.IsNullOrWhiteSpace(rule.EntityId) && !Guid.TryParse(rule.EntityId, out _)) rule.Enabled = false;
        }
        var labelRow = rows.SingleOrDefault(row => row.Section == "DeviceLabels" && row.Key == "LabelsJson");
        if (labelRow is not null)
        {
            var labels = JsonSerializer.Deserialize<Dictionary<string, string>>(labelRow.Value) ?? new();
            foreach (var (legacy, device) in aliases)
            {
                var oldKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("shelly:" + legacy.Replace("shelly:", "", StringComparison.OrdinalIgnoreCase).Trim().ToLowerInvariant())));
                if (labels.TryGetValue(oldKey, out var label)) labels.TryAdd(DeviceNameService.LabelKey(device.ToString("D")), label);
            }
            labelRow.Value = JsonSerializer.Serialize(labels);
        }
        // Configuration, identities and aliases must exist before changing durable history keys.
        await db.SaveChangesAsync(ct);
        if (inverter is { } source)
        {
            var identity = source.ToString("D");
            await db.Readings.Where(row => row.SolarObservedAt != null && EF.Functions.Collate(row.SolarDeviceSn!, "Latin1_General_100_BIN2") == deye.DeviceSn)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.SolarDeviceSn, identity), ct);
            await db.ExportReadings.Where(row => EF.Functions.Collate(row.DeviceSn, "Latin1_General_100_BIN2") == deye.DeviceSn)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.DeviceSn, identity), ct);
            var confirmation = rows.SingleOrDefault(row => row.Section == "SolarEstimate" && row.Key == "DeyeConfirmedDeviceSn");
            if (confirmation?.Value == deye.DeviceSn) confirmation.Value = identity;
        }
        db.AppSettings.RemoveRange(rows.Where(row => row.Section == DeyeCloudOptions.Section && row.Key is "AppSecret" or "Password"
            || row.Section == ShellyOptions.Section && row.Key == "AuthKey"));
        var marker = rows.SingleOrDefault(row => row.Section == MarkerSection && row.Key == MarkerKey);
        if (marker is null) db.AppSettings.Add(new() { Section = MarkerSection, Key = MarkerKey, Value = "1" });
        else marker.Value = "1";
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        foreach (var instance in imported) changes.Publish(installation, instance);
        return true;
    }
    private void AddConfiguration(DeyeSolarDbContext db, IntegrationInstanceEntity instance, JsonElement values,
        Dictionary<string, string> credentials, DateTimeOffset now)
    {
        var encrypted = secrets.Encrypt(instance.InstallationId, instance.Id, 1, credentials);
        if (!credentials.OrderBy(item => item.Key).SequenceEqual(secrets.Decrypt(instance.InstallationId, instance.Id, 1, encrypted).OrderBy(item => item.Key)))
            throw new InvalidOperationException("Legacy credential encryption could not be verified.");
        db.IntegrationConfigurations.Add(new()
        {
            InstanceId = instance.Id,
            Revision = 1,
            ValuesJson = values.GetRawText(),
            SecretsCiphertext = encrypted,
            CreatedAt = now
        });
    }
    private static IntegrationInstanceEntity Instance(IntegrationProviderDescriptor provider, string installation,
        DateTimeOffset now, string name, bool enabled) => new()
        {
            Id = StableId(installation + ":legacy-instance:" + provider.ProviderId),
            InstallationId = installation,
            ProviderId = provider.ProviderId,
            Name = name,
            PackageVersion = provider.PackageVersion,
            PackageDigest = provider.PackageDigest,
            DescriptorDigest = provider.DescriptorDigest,
            ConfigurationVersion = provider.ConfigurationVersion,
            State = enabled ? "enabled" : "disabled",
            CreatedAt = now,
            UpdatedAt = now
        };
    private static IntegrationDeviceBindingEntity Binding(IntegrationInstanceEntity instance, string remote, string channel,
        string kind, string name) => new()
        {
            Id = StableId(instance.Id.ToString("D") + ":" + kind + ":" + remote + ":" + channel),
            InstanceId = instance.Id,
            RemoteId = remote,
            Channel = channel,
            Kind = kind,
            Name = name,
            MetadataJson = kind == "socket" ? IntegrationJson.Element(new { capabilities = new { canSwitch = true, canMeasurePower = false } }).GetRawText() : "{}"
        };
    private static Guid StableId(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes("solar-legacy-v1:" + value)).AsSpan(0, 16));
    private static void ApplyRows<T>(IEnumerable<AppSetting> rows, string section, T target)
    {
        foreach (var row in rows.Where(row => row.Section == section))
        {
            var property = typeof(T).GetProperty(row.Key);
            if (property?.CanWrite != true) continue;
            try
            {
                property.SetValue(target, property.PropertyType == typeof(string) ? row.Value
                : System.ComponentModel.TypeDescriptor.GetConverter(property.PropertyType).ConvertFromInvariantString(row.Value));
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException) { }
        }
    }
}
