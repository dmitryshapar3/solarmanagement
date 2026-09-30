using DeyeSolar.Domain.Options;

namespace DeyeSolar.Web.Services;

internal static class ExportSalesRange
{
    internal static (DateTimeOffset Start, DateTimeOffset End, DateTimeOffset DataStart, DateTimeOffset DataEnd,
        DateOnly Today) Create(ExportSalesRequest request, SolarSalesOptions options, DateTimeOffset now)
    {
        options.Validate();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var from = request.Period switch
        {
            ExportSalesPeriod.Day => request.Date,
            ExportSalesPeriod.Month => new DateOnly(request.Date.Year, request.Date.Month, 1),
            ExportSalesPeriod.Year => new DateOnly(request.Date.Year, 1, 1),
            ExportSalesPeriod.Custom => request.From ?? throw new ArgumentException("A custom period requires a start date."),
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };
        var until = request.Period switch
        {
            ExportSalesPeriod.Day => from.AddDays(1),
            ExportSalesPeriod.Month => from.AddMonths(1),
            ExportSalesPeriod.Year => from.AddYears(1),
            _ => (request.Through ?? throw new ArgumentException("A custom period requires an end date.")).AddDays(1)
        };
        if (from > today || until <= from || until.DayNumber - from.DayNumber > 366 || from.Year < 2000
            || request.Period == ExportSalesPeriod.Custom && until > today.AddDays(1))
            throw new ArgumentException("Choose a valid calendar period of at most 366 days.");
        DateTimeOffset Utc(DateOnly date) => new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue), zone), TimeSpan.Zero);
        var start = Utc(from);
        var end = Utc(until);
        var contractStart = Utc(options.ContractStartDate);
        var current = now.UtcDateTime;
        var completeHour = new DateTimeOffset(current.Year, current.Month, current.Day, current.Hour, 0, 0, TimeSpan.Zero);
        return (start, end, start > contractStart ? start : contractStart,
            end < completeHour ? end : completeHour, today);
    }

    internal static IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> Buckets(ExportSalesRequest request,
        DateTimeOffset start, DateTimeOffset end, string timeZoneId)
    {
        var result = new List<(DateTimeOffset, DateTimeOffset)>();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        for (var cursor = start; cursor < end;)
        {
            var local = TimeZoneInfo.ConvertTime(cursor, zone).DateTime;
            var next = request.Period == ExportSalesPeriod.Day ? cursor.AddHours(1)
                : new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(request.Period == ExportSalesPeriod.Year
                    ? local.AddMonths(1) : local.AddDays(1), zone), TimeSpan.Zero);
            if (next > end) next = end;
            result.Add((cursor, next));
            cursor = next;
        }
        return result;
    }
}
