using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeyeSolar.Domain.Options;
using DeyeSolar.Web.Api;
using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Integrations;
using DeyeSolar.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Redesign;

public sealed record InstallationSettingsDto(SiteSettingsDto Site, PollingSettingsDto Polling,
    DisplaySettingsDto Display, Guid? PrimaryInverterId, IReadOnlyList<IntegrationSourceInverterDto> Inverters,
    string Version, IReadOnlyDictionary<Guid, IntegrationVersionGuard> IntegrationVersions);
public sealed record InstallationSettingsChange(SiteSettingsDto Site, PollingSettingsDto Polling,
    DisplaySettingsDto Display, Guid? PrimaryInverterId, string ExpectedVersion,
    Dictionary<Guid, IntegrationVersionGuard>? ExpectedIntegrationVersions = null);

public sealed class InstallationSettingsService(IDbContextFactory<DeyeSolarDbContext> factory,
    AppSettingsService settings, InteractiveSecurityContext security, IntegrationChangeNotifier changes)
{
    private static readonly string[] Sections = [SolarEstimateOptions.Section, SolarSalesOptions.Section, PollingOptions.Section, DisplayOptions.Section];
    public async Task<InstallationSettingsDto> LoadAsync(CancellationToken ct = default)
    {
        await security.EnsureAsync(InstallationPermission.Read, ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var atomic = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var result = await ReadAsync(db, ct);
        await atomic.CommitAsync(ct);
        return result;
    }
    public async Task<InstallationSettingsDto> SaveAsync(InstallationSettingsChange draft, CancellationToken ct = default)
    {
        await security.EnsureAsync(InstallationPermission.ManageSettings, ct);
        if (!SiteSettingsService.TryValidate(draft.Site, out var error)) throw new ArgumentException(error);
        if (draft.Polling is null || draft.Display is null || draft.Polling.IntervalSeconds is < 1 or > 3600
            || !TimeZoneInfo.TryFindSystemTimeZoneById(draft.Display.TimeZoneId, out _))
            throw new ArgumentException("Enter a valid polling interval and time zone.");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var atomic = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        await InstallationMutationGuard.EnsureEnabledAsync(db, ct);
        var current = await ReadAsync(db, ct);
        if (string.IsNullOrWhiteSpace(draft.ExpectedVersion) || current.Version != draft.ExpectedVersion)
            throw new IntegrationRequestException("settings_conflict", "Installation settings changed. Reload before saving.", 409);
        var notifications = new List<Guid>();
        if (current.PrimaryInverterId != draft.PrimaryInverterId)
        {
            await security.EnsureAsync(InstallationPermission.ManageIntegrations, ct);
            var old = current.PrimaryInverterId;
            var affected = await db.IntegrationDeviceBindings.Where(b => b.Id == old || b.Id == draft.PrimaryInverterId).ToListAsync(ct);
            var selected = affected.SingleOrDefault(b => b.Id == draft.PrimaryInverterId);
            if (draft.PrimaryInverterId.HasValue && (selected is null || !selected.Enabled || selected.Kind != "inverter"))
                throw new ArgumentException("Choose an enabled inverter from this installation.");
            foreach (var id in affected.Select(b => b.InstanceId).Distinct().Order())
            {
                var instance = await IntegrationPersistenceGuard.LockInstanceAsync(db, id, ct)
                    ?? throw new IntegrationRequestException("integration_changed", "The inverter integration changed.", 409);
                if (instance.State != "enabled" || draft.ExpectedIntegrationVersions?.TryGetValue(id, out var guard) != true)
                    throw new IntegrationRequestException("integration_changed", "Reload the inverter integration before changing the primary source.", 409);
                IntegrationConfigurationIdentity.Guard(instance, guard!.ExpectedRevision, guard.PackageVersion, guard.PackageDigest, guard.DescriptorDigest);
                if (await db.IntegrationCommands.AnyAsync(c => c.InstanceId == id && (c.Status == "requested" || c.Status == "pending" || c.Status == "uncertain"), ct))
                    throw new IntegrationRequestException("command_in_progress", "Wait for pending device commands before changing the primary inverter.", 409);
                await db.IntegrationInstances.Where(i => i.Id == id).ExecuteUpdateAsync(u => u
                    .SetProperty(i => i.Generation, i => i.Generation + 1).SetProperty(i => i.UpdatedAt, DateTimeOffset.UtcNow), ct);
                notifications.Add(id);
            }
            await db.IntegrationDeviceBindings.Where(b => b.Kind == "inverter" && b.IsDefault)
                .ExecuteUpdateAsync(u => u.SetProperty(b => b.IsDefault, false), ct);
            foreach (var binding in affected) binding.IsDefault = binding.Id == draft.PrimaryInverterId;
        }
        var solar = draft.Site.SolarEstimate;
        var selectedKey = draft.PrimaryInverterId?.ToString("D") ?? "";
        if (solar.DeyeSolarPowerIsPvDcConfirmed && (selectedKey.Length == 0 || solar.DeyeSolarPowerConfirmedDeviceSn != selectedKey))
            throw new ArgumentException("Confirm PV readings for the selected primary inverter.");
        await AppSettingsService.ApplySectionsAsync(db, new Dictionary<string, object>
        {
            [SolarEstimateOptions.Section] = new
            {
                solar.Latitude, solar.Longitude, LocationLabel = solar.LocationLabel.Trim(), solar.TimeZoneId,
                solar.Roof1Kwp, solar.Roof2Kwp, solar.Roof1Tilt, solar.Roof2Tilt, solar.Roof1Azimuth, solar.Roof2Azimuth,
                solar.DeyeSolarPowerIsPvDcConfirmed, DeyeConfirmedDeviceSn = solar.DeyeSolarPowerIsPvDcConfirmed ? selectedKey : ""
            },
            [SolarSalesOptions.Section] = draft.Site.SolarSales,
            [PollingOptions.Section] = draft.Polling, [DisplayOptions.Section] = draft.Display
        }, ct);
        await security.EnsureAsync(InstallationPermission.ManageSettings, ct);
        await db.SaveChangesAsync(ct);
        var result = await ReadAsync(db, ct);
        await atomic.CommitAsync(ct);
        settings.Reload();
        foreach (var id in notifications) changes.Publish(db.InstallationId!, id);
        return result;
    }
    private async Task<InstallationSettingsDto> ReadAsync(DeyeSolarDbContext db, CancellationToken ct)
    {
        var rows = await db.AppSettings.AsNoTracking().Where(s => Sections.Contains(s.Section)).OrderBy(s => s.Section).ThenBy(s => s.Key).ToListAsync(ct);
        T Load<T>(string section) where T : new()
        {
            var value = settings.Defaults<T>(section);
            foreach (var property in SettingsSchema.Properties(section, typeof(T)))
                if (rows.SingleOrDefault(s => s.Section == section && s.Key == property.Name) is { } row) property.Apply(value!, row.Value);
            return value;
        }
        var solar = Load<SolarEstimateOptions>(SolarEstimateOptions.Section);
        var sales = Load<SolarSalesOptions>(SolarSalesOptions.Section);
        var polling = Load<PollingOptions>(PollingOptions.Section);
        var display = Load<DisplayOptions>(DisplayOptions.Section);
        var bindings = await (from binding in db.IntegrationDeviceBindings.AsNoTracking()
            join instance in db.IntegrationInstances.AsNoTracking() on binding.InstanceId equals instance.Id
            where binding.Kind == "inverter" && binding.Enabled && instance.State == "enabled"
            orderby binding.Name, binding.Id select new { Binding = binding, Instance = instance }).ToListAsync(ct);
        var primary = bindings.SingleOrDefault(b => b.Binding.IsDefault)?.Binding.Id;
        var key = primary?.ToString("D") ?? "";
        var confirmed = solar.DeyeSolarPowerIsPvDcConfirmed && key.Length > 0 && solar.DeyeConfirmedDeviceSn == key;
        var site = new SiteSettingsDto(new(solar.Latitude, solar.Longitude, solar.LocationLabel, solar.TimeZoneId,
            solar.Roof1Kwp, solar.Roof2Kwp, solar.Roof1Tilt, solar.Roof2Tilt, solar.Roof1Azimuth, solar.Roof2Azimuth,
            confirmed, confirmed ? key : ""), new(sales.ContractStartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), sales.TimeZoneId, sales.PayNegativePrices), key);
        var fingerprint = JsonSerializer.Serialize(new { site, polling, display, Sources = bindings.Select(b => new
        { BindingId = b.Binding.Id, b.Binding.IsDefault, InstanceId = b.Instance.Id, b.Instance.Revision, b.Instance.Generation, b.Instance.PackageDigest, b.Instance.DescriptorDigest }) });
        var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint))).ToLowerInvariant();
        return new(site, polling.ToDto(), display.ToDto(), primary,
            bindings.Select(b => new IntegrationSourceInverterDto(b.Binding.Id, b.Binding.Name, b.Binding.IsDefault)).ToArray(), version,
            bindings.DistinctBy(b => b.Instance.Id).ToDictionary(b => b.Instance.Id,
                b => new IntegrationVersionGuard(b.Instance.Revision, b.Instance.PackageVersion, b.Instance.PackageDigest, b.Instance.DescriptorDigest)));
    }
}
