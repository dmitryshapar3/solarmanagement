using SolarPowerBasis = DeyeSolar.Domain.Models.SolarPowerBasis;
using SolarManagement.Inverters.Contracts;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Web.Services;

public enum SolarHistoryPeriod { Today, Week, Month }

public sealed record SolarHistoryPowerRange(double LowerKw, double UpperKw);

public sealed record SolarHistoryPoint(DateTimeOffset Timestamp, SolarHistoryPowerRange? Possible, double? ActualKw);

public sealed record SolarHistoryResult(DateTimeOffset Start, DateTimeOffset End, string TimeZoneId,
    IReadOnlyList<SolarHistoryPoint> Points, string? WeatherError = null, string? ActualError = null)
{
    public DateOnly SelectedDate { get; init; }
    public DateOnly Today { get; init; }
}

public interface ISolarHistoryService
{
    Task<SolarHistoryResult> ReadAsync(SolarHistoryPeriod period, CancellationToken ct, DateOnly? endDate = null);
}

public sealed class SolarHistoryService(ISolarHistoryRadiationSource weather, ISolarHistoryStore store,
    IOptionsMonitor<SolarEstimateOptions> options, IOptionsMonitor<InverterConnectionOptions> inverterOptions,
    TimeProvider clock, ILogger<SolarHistoryService> logger) : ISolarHistoryService, IDisposable
{
    private readonly SemaphoreSlim _weatherGate = new(1, 1);
    private WeatherCache? _cache;

    public async Task<SolarHistoryResult> ReadAsync(SolarHistoryPeriod period, CancellationToken ct, DateOnly? endDate = null)
    {
        ct.ThrowIfCancellationRequested();
        var now = clock.GetUtcNow();
        var config = options.CurrentValue;
        if (config.Roof1Kwp == 0 && config.Roof2Kwp == 0)
        {
            var unconfiguredToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(config.TimeZoneId)).DateTime);
            var unconfiguredDate = endDate ?? unconfiguredToday;
            var (unconfiguredStart, unconfiguredEnd) = SolarHistoryAggregation.Range(period, now, config.TimeZoneId, unconfiguredDate);
            var emptyPoints = new List<SolarHistoryPoint>();
            for (var time = unconfiguredStart; time < unconfiguredEnd; time = time.AddHours(1)) emptyPoints.Add(new(time, null, null));
            return new(unconfiguredStart, unconfiguredEnd, config.TimeZoneId, emptyPoints,
                WeatherError: Tenancy.TenantRuntimeOptions.ConfigureSiteMessage,
                ActualError: "Configure the selected inverter and confirm its PV power type in Settings.")
                { SelectedDate = unconfiguredDate, Today = unconfiguredToday };
        }
        config.Validate();
        var key = SolarEstimateService.ConfigurationKey(config);
        var device = inverterOptions.CurrentValue.DeviceKey;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(config.TimeZoneId)).DateTime);
        var selectedDate = endDate ?? today;
        var (start, end) = SolarHistoryAggregation.Range(period, now, config.TimeZoneId, selectedDate);
        if (start >= end) return new(start, end, config.TimeZoneId, []) { SelectedDate = selectedDate, Today = today };

        var weatherTask = ReadWeatherAsync(config, key, now, selectedDate, ct);
        var actualTask = ReadActualAsync(config, device, start, end, now, ct);
        await Task.WhenAll(weatherTask, actualTask);
        ct.ThrowIfCancellationRequested();
        if (device != inverterOptions.CurrentValue.DeviceKey || key != SolarEstimateService.ConfigurationKey(options.CurrentValue))
            return new(start, end, config.TimeZoneId, [], ActualError: "Installation settings changed. Refresh the chart.")
                { SelectedDate = selectedDate, Today = today };

        var model = await weatherTask;
        var actual = await actualTask;
        var estimates = model.Samples.Where(p => p.Timestamp >= start && p.Timestamp < end)
            .ToDictionary(p => p.Timestamp, p =>
            {
                var estimate = SolarPowerCalculator.Calculate(
                    new(p.Timestamp, p.Roof1Gti, p.Roof2Gti, p.AirTemperatureC, p.WindSpeedMs, p.Timestamp.AddHours(1), 0)
                    { Kind = SolarRadiationKind.WeatherModel, RetrievedAt = now }, config, now);
                return new SolarHistoryPowerRange(estimate.LowerKw, estimate.UpperKw);
            });
        var points = new List<SolarHistoryPoint>();
        for (var time = start; time < end; time = time.AddHours(1))
            points.Add(new(time, estimates.TryGetValue(time, out var value) ? value : null,
                SolarHistoryAggregation.MeanPower(actual.Samples, time)));
        return new(start, end, config.TimeZoneId, points, model.Error, actual.Error)
            { SelectedDate = selectedDate, Today = today };
    }

    private async Task<WeatherCache> ReadWeatherAsync(SolarEstimateOptions config, string key,
        DateTimeOffset now, DateOnly selectedDate, CancellationToken ct)
    {
        var (start, end) = SolarHistoryAggregation.Range(SolarHistoryPeriod.Month, now, config.TimeZoneId, selectedDate);
        await _weatherGate.WaitAsync(ct);
        try
        {
            if (_cache is { } cached && cached.Key == key && cached.Start == start && cached.End == end
                && cached.ExpiresAt > now) return cached;
            try
            {
                var samples = await weather.ReadAsync(config, start, end, ct);
                _cache = new(key, start, end, now.AddMinutes(15), samples, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Solar history weather unavailable ({ErrorType})", ex.GetType().Name);
                _cache = new(key, start, end, now.AddMinutes(2), [], "Weather history is unavailable. Try refreshing the chart later.");
            }
            return _cache;
        }
        finally { _weatherGate.Release(); }
    }

    private async Task<(IReadOnlyList<SolarActual> Samples, string? Error)> ReadActualAsync(
        SolarEstimateOptions config, string device, DateTimeOffset start, DateTimeOffset end, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(device) || !config.SolarPowerIsPvDcConfirmed
            || config.ConfirmedInverterKey != device)
            return ([], "Confirm the selected inverter's PV power type before comparing readings.");
        // Adjacent measurements support interpolation at either boundary without scanning
        // from an older selected period all the way through the current date.
        var through = end.AddMinutes(10) < now ? end.AddMinutes(10) : now;
        try { return (await store.ReadAsync(device, start.AddMinutes(-10), through, ct), null); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Solar actual history unavailable ({ErrorType})", ex.GetType().Name);
            return ([], "Inverter history is unavailable. Try refreshing the chart later.");
        }
    }

    public void Dispose() => _weatherGate.Dispose();

    private sealed record WeatherCache(string Key, DateTimeOffset Start, DateTimeOffset End,
        DateTimeOffset ExpiresAt, IReadOnlyList<SolarWeatherSample> Samples, string? Error);
}

internal static class SolarHistoryAggregation
{
    internal static (DateTimeOffset Start, DateTimeOffset End) Range(SolarHistoryPeriod period,
        DateTimeOffset now, string timeZoneId, DateOnly? selectedDate = null)
    {
        var days = period switch { SolarHistoryPeriod.Today => 1, SolarHistoryPeriod.Week => 7,
            SolarHistoryPeriod.Month => 30, _ => throw new ArgumentOutOfRangeException(nameof(period)) };
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var selected = selectedDate ?? today;
        if (selected > today || selected < today.AddDays(-29))
            throw new ArgumentOutOfRangeException(nameof(selectedDate), "The selected history date must be within the last 30 local calendar dates.");
        var localStart = selected.AddDays(1 - days).ToDateTime(TimeOnly.MinValue);
        var start = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, zone), TimeSpan.Zero);
        // A past calendar day is complete, including its 23- or 25-hour DST variant.
        // Only today's interval stops at the latest completed UTC hour.
        if (selected < today)
        {
            var localEnd = selected.AddDays(1).ToDateTime(TimeOnly.MinValue);
            return (start, new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localEnd, zone), TimeSpan.Zero));
        }
        var utc = now.UtcDateTime;
        var end = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
        return (start, end);
    }

    // Integrate only adjacent measured samples. Never carry a reading through an outage.
    // At least 90% of an hour must be observed; repeated polling must not change its weight.
    internal static double? MeanPower(IReadOnlyList<SolarActual> samples, DateTimeOffset start)
    {
        var end = start.AddHours(1);
        var relevant = samples.Where(p => p.Timestamp >= start.AddMinutes(-10) && p.Timestamp <= end.AddMinutes(10)
                && p.Basis == SolarPowerBasis.PvDc && double.IsFinite(p.PowerKw) && p.PowerKw >= 0)
            .GroupBy(p => p.Timestamp).Select(g => g.Last()).OrderBy(p => p.Timestamp).ToArray();
        var coveredSeconds = 0d;
        var integrated = 0d;
        for (var i = 1; i < relevant.Length; i++)
        {
            var left = relevant[i - 1];
            var right = relevant[i];
            var duration = (right.Timestamp - left.Timestamp).TotalSeconds;
            if (duration <= 0 || duration > 600) continue;
            var from = left.Timestamp > start ? left.Timestamp : start;
            var to = right.Timestamp < end ? right.Timestamp : end;
            if (to <= from) continue;
            var fromPower = left.PowerKw + (right.PowerKw - left.PowerKw) * (from - left.Timestamp).TotalSeconds / duration;
            var toPower = left.PowerKw + (right.PowerKw - left.PowerKw) * (to - left.Timestamp).TotalSeconds / duration;
            var seconds = (to - from).TotalSeconds;
            integrated += (fromPower + toPower) / 2 * seconds;
            coveredSeconds += seconds;
        }
        return coveredSeconds >= 3240 ? integrated / coveredSeconds : null;
    }
}
