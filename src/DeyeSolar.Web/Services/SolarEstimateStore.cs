using System.Text.Json;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Web.Services;

public sealed record CachedSolarObservation(string ConfigurationKey, SolarRadiationObservation Observation, DateTimeOffset RetrievedAt);

public interface ISolarEstimateStore
{
    Task<CachedSolarObservation?> LoadAsync(CancellationToken ct);
    Task SaveAsync(CachedSolarObservation observation, CancellationToken ct);
    Task<SolarActual?> FindActualAsync(DateTimeOffset timestamp, int toleranceSeconds, DateTimeOffset now, CancellationToken ct);
}

public sealed class SolarEstimateStore(IDbContextFactory<DeyeSolarDbContext> factory,
    IOptionsMonitor<DeyeCloudOptions> deyeOptions) : ISolarEstimateStore
{
    private const string CacheSection = "SolarEstimateCache";

    public async Task<CachedSolarObservation?> LoadAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var value = await db.AppSettings.AsNoTracking().Where(s => s.Section == CacheSection && s.Key == "LastObservation")
            .Select(s => s.Value).SingleOrDefaultAsync(ct);
        return value == null ? null : JsonSerializer.Deserialize<CachedSolarObservation>(value);
    }

    public async Task SaveAsync(CachedSolarObservation observation, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.AppSettings.SingleOrDefaultAsync(s => s.Section == CacheSection && s.Key == "LastObservation", ct);
        if (row == null)
        {
            row = new AppSetting { Section = CacheSection, Key = "LastObservation" };
            db.AppSettings.Add(row);
        }
        row.Value = JsonSerializer.Serialize(observation);
        await db.SaveChangesAsync(ct);
    }

    public async Task<SolarActual?> FindActualAsync(DateTimeOffset timestamp, int toleranceSeconds, DateTimeOffset now, CancellationToken ct)
    {
        var deviceSn = deyeOptions.CurrentValue.DeviceSn;
        if (string.IsNullOrWhiteSpace(deviceSn)) return null;
        await using var db = await factory.CreateDbContextAsync(ct);
        var earliest = timestamp.AddSeconds(-toleranceSeconds).UtcDateTime;
        var latest = (timestamp.AddSeconds(toleranceSeconds) < now ? timestamp.AddSeconds(toleranceSeconds) : now).UtcDateTime;
        // Only measurements from the currently selected inverter belong to this comparison.
        // Legacy rows lack measured time/device provenance and must remain excluded.
        var rows = await EligibleReadings(db.Readings.AsNoTracking(), deviceSn, earliest, latest)
            .Select(r => new { r.Id, r.SolarObservedAt, r.SolarProduction }).ToListAsync(ct);
        // Repeated measurements use the latest persisted valid correction, matching history.
        var nearest = rows.OrderBy(r => Math.Abs((r.SolarObservedAt!.Value - timestamp.UtcDateTime).TotalSeconds))
            .ThenBy(r => r.SolarObservedAt).ThenByDescending(r => r.Id).FirstOrDefault();
        if (deyeOptions.CurrentValue.DeviceSn != deviceSn) return null;
        return nearest == null ? null : new(new DateTimeOffset(DateTime.SpecifyKind(nearest.SolarObservedAt!.Value, DateTimeKind.Utc)),
            nearest.SolarProduction / 1000.0, SolarPowerBasis.PvDc);
    }

    internal static IQueryable<Reading> EligibleReadings(IQueryable<Reading> readings, string deviceSn,
        DateTime earliest, DateTime latest) => readings.Where(r => r.SolarDeviceSn == deviceSn
            && r.SolarObservedAt != null && r.SolarObservedAt >= earliest && r.SolarObservedAt <= latest
            && r.SolarProduction >= 0);
}
