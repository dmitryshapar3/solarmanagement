using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Redesign;

public sealed partial class RedesignQueries
{
    public async Task<ReadingsViewDto> ReadingsAsync(int hours = 6, string aggregate = "raw",
        string? cursor = null, CancellationToken ct = default)
    {
        await security.EnsureAsync(InstallationPermission.Read, ct);
        if (hours is < 1 or > 168 || aggregate is not ("raw" or "5m"))
            throw new ArgumentException("Choose raw or 5-minute readings over at most seven days.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var page = await WindowAsync(db, null, null, hours, 168, cursor, $"readings/{hours}/{aggregate}", ct);
        var query = db.Readings.AsNoTracking().Where(r => r.Id <= page.Snapshot && r.Timestamp >= page.From && r.Timestamp < page.Through);
        List<ReadingRowDto> rows;
        if (aggregate == "raw")
        {
            if (page.Before.HasValue) query = query.Where(r => r.Timestamp < page.Before || r.Timestamp == page.Before && r.Id < page.BeforeId);
            var found = await query.OrderByDescending(r => r.Timestamp).ThenByDescending(r => r.Id).Take(501).ToListAsync(ct);
            rows = found.Select(r => new ReadingRowDto(r.Id, Utc(r.Timestamp), r.InverterId,
                r.ConfigurationRevision, r.RuntimeGeneration, r.DataSource,
                r.BatterySocValid ? r.BatterySoc : null, r.BatteryTemperatureValid ? r.BatteryTemperature : null,
                r.BatteryVoltageValid ? r.BatteryVoltage : null, r.BatteryPowerValid ? r.BatteryPower : null,
                r.BatteryCurrentValid ? r.BatteryCurrent : null, r.SolarPowerValid ? r.SolarProduction : null,
                r.GridPowerValid ? r.GridConsumption : null, r.LoadPowerValid ? r.LoadPower : null,
                r.SolarObservedAt.HasValue ? Utc(r.SolarObservedAt.Value) : null)).ToList();
        }
        else
        {
            var epoch = new DateTime(2000, 1, 1);
            var averages = query.GroupBy(r => new
            {
                Bucket = EF.Functions.DateDiffMinute(epoch, r.Timestamp) / 5,
                r.InverterId, r.ConfigurationRevision, r.RuntimeGeneration, r.DataSource
            }).Select(g => new
            {
                Id = g.Max(r => r.Id), At = epoch.AddMinutes(g.Key.Bucket * 5),
                g.Key.InverterId, g.Key.ConfigurationRevision, g.Key.RuntimeGeneration, g.Key.DataSource,
                Soc = g.Average(r => r.BatterySocValid ? (double?)r.BatterySoc : null),
                Temperature = g.Average(r => r.BatteryTemperatureValid ? (double?)r.BatteryTemperature : null),
                Voltage = g.Average(r => r.BatteryVoltageValid ? (double?)r.BatteryVoltage : null),
                BatteryPower = g.Average(r => r.BatteryPowerValid ? (double?)r.BatteryPower : null),
                Current = g.Average(r => r.BatteryCurrentValid ? (double?)r.BatteryCurrent : null),
                Solar = g.Average(r => r.SolarPowerValid ? (double?)r.SolarProduction : null),
                Grid = g.Average(r => r.GridPowerValid ? (double?)r.GridConsumption : null),
                Load = g.Average(r => r.LoadPowerValid ? (double?)r.LoadPower : null),
                SolarAt = g.Max(r => r.SolarObservedAt)
            });
            if (page.Before.HasValue) averages = averages.Where(r => r.At < page.Before || r.At == page.Before && r.Id < page.BeforeId);
            var found = await averages.OrderByDescending(r => r.At).ThenByDescending(r => r.Id).Take(501).ToListAsync(ct);
            rows = found.Select(r => new ReadingRowDto(r.Id, Utc(r.At), r.InverterId, r.ConfigurationRevision,
                r.RuntimeGeneration, r.DataSource, r.Soc, r.Temperature, r.Voltage, r.BatteryPower, r.Current,
                r.Solar, r.Grid, r.Load, r.SolarAt.HasValue ? Utc(r.SolarAt.Value) : null)).ToList();
        }
        var hasMore = rows.Count > 500;
        var items = rows.Take(500).ToArray();
        var sorted = items.OrderBy(r => r.Timestamp).ThenBy(r => r.Id).ToArray();
        var gaps = new List<ReadingGapDto>();
        for (var i = 1; i < sorted.Length; i++)
        {
            var a = sorted[i - 1]; var b = sorted[i];
            if (b.Timestamp - a.Timestamp > TimeSpan.FromMinutes(10))
                gaps.Add(new(a.Timestamp, b.Timestamp, "measurement_gap"));
            else if (a.InverterId != b.InverterId || a.ConfigurationRevision != b.ConfigurationRevision
                || a.RuntimeGeneration != b.RuntimeGeneration || a.DataSource != b.DataSource)
                gaps.Add(new(a.Timestamp, b.Timestamp, "source_changed"));
        }
        if (page.Before is null && (items.Length == 0 || Utc(page.Through) - items[0].Timestamp > TimeSpan.FromMinutes(10)))
            gaps.Add(new(items.FirstOrDefault()?.Timestamp ?? Utc(page.From), Utc(page.Through), "measurement_gap"));
        if (!hasMore && sorted.Length > 0 && sorted[0].Timestamp - Utc(page.From) > TimeSpan.FromMinutes(10))
            gaps.Add(new(Utc(page.From), sorted[0].Timestamp, "measurement_gap"));
        var last = items.LastOrDefault();
        var next = hasMore && last is not null ? Encode(page with { Before = last.Timestamp.UtcDateTime, BeforeId = last.Id }) : null;
        return new(Utc(page.From), Utc(page.Through), aggregate, items, gaps, next,
            gaps.Count > 0 || items.Any(r => r.BatterySoc is null || r.SolarProduction is null));
    }
}
