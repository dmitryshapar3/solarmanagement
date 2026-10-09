using DeyeSolar.Web.Api;
using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Integrations;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using DeyeSolar.Domain.Options;

namespace DeyeSolar.Web.Redesign;

public sealed record DeviceDetailsDto(string Id, string Name, DeviceDto? Device, string? ProviderId, string? Model,
    Guid? InstanceId, Guid? SourceInverterId, int PhaseCount, IReadOnlyList<TriggerRuleDto> ControllingRules,
    DateTimeOffset? LastConfirmedSwitch, DateTimeOffset? AddedAt, bool CanSwitch, bool SupportsHistory)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ProviderDisplayName { get; init; }
}
public sealed record ConnectedServiceDto(Guid Id, string ProviderId, string Name, string Kind,
    string Status, bool Enabled, int SocketCount, string? DefaultInverterName, DateTimeOffset? LastReadingAt,
    DateTimeOffset? LastConfirmedSwitch);
public sealed record IntegrationStatusViewDto(IReadOnlyList<ConnectedServiceDto> Services,
    DateTimeOffset? ForecastRetrievedAt, DateTimeOffset? LatestStoredPriceAt, int MissingPriceHours,
    DateTimeOffset PriceWindowStart, DateTimeOffset PriceWindowEnd, string SettlementTimeZoneId)
{
    public string PriceSource { get; init; } = "pse";
    public decimal? ManualPricePlnPerKwh { get; init; }
}

public sealed partial class RedesignQueries
{
    public async Task<DeviceDetailsDto?> DeviceAsync(string id, CancellationToken ct = default)
    {
        await security.EnsureAsync(InstallationPermission.Read, ct);
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl)) throw new ArgumentException("Choose a valid device.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var binding = Guid.TryParse(id, out var guid)
            ? await db.IntegrationDeviceBindings.AsNoTracking().SingleOrDefaultAsync(b => b.Id == guid, ct) : null;
        var inventory = devices.Current?.FirstOrDefault(d => d.Id == id || Guid.TryParse(d.Id, out var parsed) && parsed == guid);
        if (binding is null && inventory is null) return null;
        var device = inventory is null ? null : (await deviceNames.DescribeAsync([inventory], ct)).Single();
        var instance = binding is null ? null : await db.IntegrationInstances.AsNoTracking().SingleOrDefaultAsync(i => i.Id == binding.InstanceId, ct);
        string? providerDisplayName = null;
        if (instance is not null && catalog is not null)
        {
            try
            {
                var descriptor = await catalog.GetAsync(instance.ProviderId, instance.PackageVersion, ct);
                if (descriptor.PackageDigest == instance.PackageDigest && descriptor.DescriptorDigest == instance.DescriptorDigest)
                    providerDisplayName = descriptor.DisplayName;
            }
            catch (InvalidOperationException) { /* Unavailable catalog metadata does not replace saved device evidence. */ }
        }
        string? model = null;
        if (binding is not null)
        {
            try
            {
            using var metadata = JsonDocument.Parse(binding.MetadataJson);
            if (metadata.RootElement.ValueKind == JsonValueKind.Object
                && metadata.RootElement.TryGetProperty("model", out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 and <= 128 } reported && !reported.Any(char.IsControl)) model = reported;
            }
            catch (JsonException) { /* Unsupported historical metadata is omitted. */ }
        }
        var canonicalId = binding?.Id.ToString("D") ?? id;
        var controlling = await db.TriggerRules.AsNoTracking().Where(r => r.EntityId == canonicalId).ToListAsync(ct);
        var confirmation = binding is null ? null : await db.IntegrationCommands.Where(c => c.DeviceId == binding.Id && c.Status == "acknowledged")
            .Select(c => c.CompletedAt).MaxAsync(ct);
        var association = binding is null ? null : IntegrationSocketAssociation.Read(binding);
        var canSwitch = binding is { Kind: "socket", Enabled: true } && instance?.State == "enabled"
            && IntegrationCapabilities.ReadSocket(binding).CanSwitch;
        return new DeviceDetailsDto(id, device?.Name ?? IntegrationDeviceDisplayName.Read(binding!) ?? binding!.Name, device, instance?.ProviderId, model, binding?.InstanceId,
            association?.SourceInverterId, association?.PhaseCount ?? 1, controlling.Select(r => r.ToDto()).ToArray(),
            confirmation, binding?.AddedAt, canSwitch, binding?.Kind == "socket") { ProviderDisplayName = providerDisplayName };
    }

    public async Task<IntegrationStatusViewDto> IntegrationsAsync(CancellationToken ct = default)
    {
        await security.EnsureAsync(InstallationPermission.Read, ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var instances = await db.IntegrationInstances.AsNoTracking().OrderBy(i => i.Name).ToListAsync(ct);
        var bindings = await db.IntegrationDeviceBindings.AsNoTracking().ToListAsync(ct);
        var services = new List<ConnectedServiceDto>();
        var now = clock.GetUtcNow();
        foreach (var instance in instances)
        {
            var owned = bindings.Where(b => b.InstanceId == instance.Id && b.Enabled).ToArray();
            var ids = owned.Select(b => b.Id).ToArray();
            var inverterIds = owned.Where(b => b.Kind == "inverter").Select(b => b.Id).ToArray();
            var reading = await db.Readings.Where(r => r.InverterId.HasValue && inverterIds.Contains(r.InverterId.Value)
                && (r.BatterySocValid || r.SolarPowerValid)).Select(r => (DateTime?)r.Timestamp).MaxAsync(ct);
            var confirmation = await db.IntegrationCommands.Where(c => c.InstanceId == instance.Id && c.Status == "acknowledged")
                .Select(c => c.CompletedAt).MaxAsync(ct);
            var socketIds = ids.Select(id => id.ToString("D")).ToArray();
            var observation = await db.ActivityEvents.Where(e => e.Kind == "device.observed" && e.State.HasValue
                && socketIds.Contains(e.DeviceId!)).Select(e => (DateTime?)e.OccurredAt).MaxAsync(ct);
            var kind = owned.Any(b => b.Kind == "inverter") ? "inverter" : owned.Any(b => b.Kind == "socket") ? "socket" : "unknown";
            var evidence = kind == "inverter" ? reading : observation;
            var enabled = instance.State == "enabled";
            var status = !enabled ? "not_connected" : evidence is { } at && now - Utc(at) <= TimeSpan.FromMinutes(10)
                ? "connected" : "needs_attention";
            services.Add(new(instance.Id, instance.ProviderId, instance.Name, kind, status, enabled,
                owned.Count(b => b.Kind == "socket"), owned.FirstOrDefault(b => b.IsDefault)?.Name,
                reading.HasValue ? Utc(reading.Value) : null, confirmation));
        }
        var salesRows = await db.AppSettings.Where(s => s.Section == SolarSalesOptions.Section).ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        var sales = new SolarSalesOptions();
        foreach (var property in DeyeSolar.Web.Data.SettingsSchema.Properties(SolarSalesOptions.Section, typeof(SolarSalesOptions)))
            if (salesRows.TryGetValue(property.Name, out var value)) property.Apply(sales, value);
        var zoneId = sales.TimeZoneId;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var start = TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue), zone);
        var end = TimeZoneInfo.ConvertTimeToUtc(date.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);
        List<DateTime> priceTimes;
        DateTime? latestPrice;
        if (sales.PriceSource == "manual") { priceTimes = []; latestPrice = null; }
        else if (sales.PriceSource == "feed")
        {
            var sourceKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sales.PriceFeedUrl))).ToLowerInvariant();
            priceTimes = await db.ExportFeedPrices.Where(p => p.SourceKey == sourceKey && p.StartUtc >= start && p.StartUtc < end).Select(p => p.StartUtc).ToListAsync(ct);
            latestPrice = await db.ExportFeedPrices.Where(p => p.SourceKey == sourceKey).Select(p => (DateTime?)p.StartUtc).MaxAsync(ct);
        }
        else
        {
            priceTimes = await db.ExportPrices.Where(p => p.StartUtc >= start && p.StartUtc < end).Select(p => p.StartUtc).ToListAsync(ct);
            latestPrice = await db.ExportPrices.Select(p => (DateTime?)p.StartUtc).MaxAsync(ct);
        }
        var missing = sales.PriceSource == "manual" ? 0 : Enumerable.Range(0, (int)(end - start).TotalHours).Count(hour => Enumerable.Range(0, 4).Any(quarter => !priceTimes.Contains(start.AddHours(hour).AddMinutes(quarter * 15))));
        return new(services, solarEstimate.Current.Estimate?.Observation.RetrievedAt,
            latestPrice.HasValue ? Utc(latestPrice.Value) : null, missing, Utc(start), Utc(end), zoneId)
        { PriceSource = sales.PriceSource, ManualPricePlnPerKwh = sales.PriceSource == "manual" ? sales.ManualPricePlnPerKwh : null };
    }
}
