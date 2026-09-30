using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Services;

public interface ISolarHistoryStore
{
    Task<IReadOnlyList<SolarActual>> ReadAsync(string deviceSn, DateTimeOffset start,
        DateTimeOffset end, CancellationToken ct);
}

public sealed class SolarHistoryStore(IDbContextFactory<DeyeSolarDbContext> factory) : ISolarHistoryStore
{
    public async Task<IReadOnlyList<SolarActual>> ReadAsync(string deviceSn, DateTimeOffset start,
        DateTimeOffset end, CancellationToken ct)
    {
        if (end <= start || end - start > TimeSpan.FromDays(31))
            throw new ArgumentOutOfRangeException(nameof(end), "History must cover a positive interval of at most 31 days.");
        if (string.IsNullOrWhiteSpace(deviceSn)) return [];
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await LatestMeasurements(db.Readings.AsNoTracking(), deviceSn, start.UtcDateTime, end.UtcDateTime)
            .OrderBy(r => r.SolarObservedAt)
            .Select(r => new { r.SolarObservedAt, r.SolarProduction }).ToListAsync(ct);
        return rows.Select(r => new SolarActual(
            new DateTimeOffset(DateTime.SpecifyKind(r.SolarObservedAt!.Value, DateTimeKind.Utc)),
            r.SolarProduction / 1000.0, SolarPowerBasis.PvDc)).ToArray();
    }

    internal static IQueryable<Reading> LatestMeasurements(IQueryable<Reading> readings, string deviceSn,
        DateTime start, DateTime end)
    {
        var eligible = SolarEstimateStore.EligibleReadings(readings, deviceSn, start, end)
            .Where(r => r.SolarObservedAt < end);
        // Polling repeats device measurements; a later persisted correction replaces the earlier value.
        var latestIds = eligible.GroupBy(r => r.SolarObservedAt).Select(group => group.Max(r => r.Id));
        return eligible.Where(r => latestIds.Contains(r.Id));
    }
}
