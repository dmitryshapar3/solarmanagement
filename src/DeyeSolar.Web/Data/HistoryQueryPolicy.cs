using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Data;

public sealed record HistoryQueryRange(DateTime Cutoff, int Take, string Filter);

/// <summary>One bounded, descending history policy for browser and API consumers.</summary>
public static class HistoryQueryPolicy
{
    public const int MaximumRows = 1000;
    public const int MaximumHours = 168;
    public static DateTime Cutoff(DateTimeOffset now, int? hours)
        => now.UtcDateTime.AddHours(-Math.Clamp(hours ?? 6, 1, MaximumHours));
    public static HistoryQueryRange Range(DateTimeOffset now, int? hours, int? take = null, string? filter = null)
        => new(Cutoff(now, hours),
            Math.Clamp(take ?? MaximumRows, 1, MaximumRows), (filter ?? "ALL").Trim().ToUpperInvariant());
    public static IQueryable<Reading> Readings(IQueryable<Reading> rows, HistoryQueryRange range)
        => rows.AsNoTracking().Where(row => row.Timestamp >= range.Cutoff)
            .OrderByDescending(row => row.Timestamp).Take(range.Take);
    public static IQueryable<RuleRunLog> Runs(IQueryable<RuleRunLog> rows, HistoryQueryRange range)
    {
        var query = rows.AsNoTracking().Where(row => row.Timestamp >= range.Cutoff);
        query = range.Filter switch
        {
            "ON" => query.Where(row => row.Action == "ON"),
            "OFF" => query.Where(row => row.Action == "OFF"),
            "CHANGES" => query.Where(row => row.Action != "NO_CHANGE"),
            _ => query
        };
        return query.OrderByDescending(row => row.Timestamp).Take(range.Take);
    }
}
