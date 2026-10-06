using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using DeyeSolar.Web.Services;
using Microsoft.Extensions.Options;
using SolarManagement.Inverters.Contracts;

namespace DeyeSolar.Web.Redesign;

public sealed record ProductionHourDto(DateTimeOffset Timestamp, double? ActualKw, double? ObservedEnergyKwh,
    double CoveredSeconds, double ExpectedSeconds, double? ExpectedKw, double? LowerKw, double? UpperKw, bool Partial);
public sealed record ProductionDayDto(DateOnly Date, double? ObservedEnergyKwh, double CoveredSeconds,
    double ExpectedSeconds, double? ExpectedEnergyKwh, double? LowerEnergyKwh, double? UpperEnergyKwh, bool Partial);
public sealed record ProductionViewDto(DateTimeOffset Start, DateTimeOffset End, string TimeZoneId, DateOnly Date,
    DateOnly Today, IReadOnlyList<ProductionHourDto> Hours, IReadOnlyList<ProductionDayDto> Days,
    double? ObservedEnergyKwh, double? CompletedEnergyKwh, double CoveredSeconds, double ExpectedSeconds,
    double? ExpectedEnergyKwh, ProductionHourDto? BestHour, ProductionHourDto? CurrentHour,
    DateTimeOffset? Sunrise, DateTimeOffset? Sunset, DateTimeOffset? NextSunrise, DateTimeOffset? ForecastRetrievedAt,
    string? WeatherError, string? ActualError, bool Partial);

public sealed class SolarProductionService(ISolarDayForecastSource forecasts, ISolarHistoryStore store,
    IOptionsMonitor<SolarEstimateOptions> options, IOptionsMonitor<InverterConnectionOptions> inverter,
    TimeProvider clock, ILogger<SolarProductionService> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset Expires, SolarDayForecast Value)> _forecastCache = new();

    public async Task<ProductionViewDto> ReadAsync(SolarHistoryPeriod period, DateOnly? date = null, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var config = options.CurrentValue;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(config.TimeZoneId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var selected = date ?? today;
        var (start, completedEnd) = SolarHistoryAggregation.Range(period, now, config.TimeZoneId, selected);
        var end = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(selected.AddDays(1).ToDateTime(TimeOnly.MinValue), zone), TimeSpan.Zero);
        var actualEnd = end < now ? end : now;
        var device = inverter.CurrentValue.DeviceKey;
        var key = SolarEstimateService.ConfigurationKey(config);
        SolarDayForecast? model = null;
        string? weatherError = null, actualError = null;
        if (config.Roof1Kwp == 0 && config.Roof2Kwp == 0) weatherError = "Configure your solar site in Settings.";
        else
        {
            config.Validate();
            try { model = await ForecastAsync(config, key, start, end, selected, now, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Production forecast unavailable ({ErrorType}).", ex.GetType().Name);
                weatherError = "Weather data is unavailable.";
            }
        }
        IReadOnlyList<SolarActual> samples = [];
        if (string.IsNullOrWhiteSpace(device) || !config.SolarPowerIsPvDcConfirmed || config.ConfirmedInverterKey != device)
            actualError = "Confirm the selected inverter's PV power type before comparing readings.";
        else if (actualEnd > start)
            try { samples = (await store.ReadAsync(device, start.AddMinutes(-10), actualEnd.AddTicks(1), ct)).Where(p => p.Timestamp <= now).ToArray(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Production observations unavailable ({ErrorType}).", ex.GetType().Name);
                actualError = "Inverter history is unavailable.";
            }
        if (device != inverter.CurrentValue.DeviceKey || key != SolarEstimateService.ConfigurationKey(options.CurrentValue))
            throw new InvalidOperationException("Installation settings changed. Reload the chart.");
        var expected = (model?.Samples ?? []).ToDictionary(s => s.Timestamp, s =>
            SolarPowerCalculator.CalculateForecast(new(s.Timestamp, s.Roof1Gti, s.Roof2Gti,
                s.AirTemperatureC, s.WindSpeedMs, s.Timestamp.AddHours(1), 0)
                { Kind = SolarRadiationKind.WeatherModel, RetrievedAt = model!.RetrievedAt }, config, now));
        var hours = new List<ProductionHourDto>();
        var firstHour = new DateTimeOffset(start.UtcTicks - start.UtcTicks % TimeSpan.TicksPerHour, TimeSpan.Zero);
        ProductionHourDto Hour(DateTimeOffset at, DateTimeOffset through, bool progress)
        {
            var from = at < start ? start : at;
            var energy = SolarEnergyIntegration.Integrate(samples, from, through);
            expected.TryGetValue(at, out var estimate);
            var mean = !progress && through <= completedEnd && energy.CoveredSeconds >= energy.ExpectedSeconds * .9 && energy.CoveredSeconds > 0
                ? energy.EnergyKwh * 3600 / energy.CoveredSeconds : null;
            return new(at, mean, through <= actualEnd ? energy.EnergyKwh : null, energy.CoveredSeconds,
                energy.ExpectedSeconds, estimate?.CentralKw, estimate?.LowerKw, estimate?.UpperKw, energy.Partial);
        }
        for (var at = firstHour; at < end; at = at.AddHours(1))
        {
            var through = at.AddHours(1) < end ? at.AddHours(1) : end;
            var isComplete = through <= completedEnd;
            var hour = isComplete ? Hour(at, through, false)
                : new ProductionHourDto(at, null, null, 0, (through - (at < start ? start : at)).TotalSeconds,
                    expected.GetValueOrDefault(at)?.CentralKw, expected.GetValueOrDefault(at)?.LowerKw, expected.GetValueOrDefault(at)?.UpperKw, true);
            hours.Add(hour);
        }
        var currentStart = new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerHour, TimeSpan.Zero);
        var current = selected == today && currentStart >= start && currentStart < end && now > currentStart ? Hour(currentStart, now, true) : null;
        var days = new List<ProductionDayDto>();
        double? ForecastEnergy(DateTimeOffset a, DateTimeOffset b, Func<SolarPowerEstimate, double> metric)
        {
            var parts = expected.Where(p => p.Key < b && p.Key.AddHours(1) > a).ToArray();
            var covered = parts.Sum(p => ((p.Key.AddHours(1) < b ? p.Key.AddHours(1) : b) - (p.Key > a ? p.Key : a)).TotalSeconds);
            if (covered < (b - a).TotalSeconds) return null;
            return parts.Sum(p => metric(p.Value) * ((p.Key.AddHours(1) < b ? p.Key.AddHours(1) : b) - (p.Key > a ? p.Key : a)).TotalSeconds / 3600);
        }
        for (var local = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, zone).DateTime); local <= selected; local = local.AddDays(1))
        {
            var a = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local.ToDateTime(TimeOnly.MinValue), zone), TimeSpan.Zero);
            var b = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local.AddDays(1).ToDateTime(TimeOnly.MinValue), zone), TimeSpan.Zero);
            var through = b < actualEnd ? b : actualEnd;
            var energy = SolarEnergyIntegration.Integrate(samples, a, through);
            days.Add(new(local, energy.EnergyKwh, energy.CoveredSeconds, energy.ExpectedSeconds,
                ForecastEnergy(a, b, e => e.CentralKw), ForecastEnergy(a, b, e => e.LowerKw),
                ForecastEnergy(a, b, e => e.UpperKw), energy.Partial));
        }
        var total = SolarEnergyIntegration.Integrate(samples, start, actualEnd);
        var completed = SolarEnergyIntegration.Integrate(samples, start, completedEnd);
        return new(start, end, config.TimeZoneId, selected, today, hours, days,
            total.EnergyKwh, completed.EnergyKwh, total.CoveredSeconds, total.ExpectedSeconds,
            ForecastEnergy(start, end, e => e.CentralKw), hours.Where(h => h.ActualKw.HasValue).OrderByDescending(h => h.ActualKw).FirstOrDefault(),
            current, model?.Sunrise, model?.Sunset, model?.NextSunrise, model?.RetrievedAt,
            weatherError, actualError, total.Partial);
    }

    private async Task<SolarDayForecast> ForecastAsync(SolarEstimateOptions config, string key,
        DateTimeOffset start, DateTimeOffset end, DateOnly selected, DateTimeOffset now, CancellationToken ct)
    {
        var cacheKey = $"{key}/{start.UtcTicks}/{end.UtcTicks}";
        await _gate.WaitAsync(ct);
        try
        {
            if (_forecastCache.TryGetValue(cacheKey, out var cached) && cached.Expires > now) return cached.Value;
            var value = await forecasts.ReadAsync(config, start, end, selected, ct);
            foreach (var expired in _forecastCache.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToArray()) _forecastCache.Remove(expired);
            if (_forecastCache.Count >= 32) _forecastCache.Remove(_forecastCache.Keys.First());
            _forecastCache[cacheKey] = (now.AddMinutes(15), value);
            return value;
        }
        finally { _gate.Release(); }
    }
}
