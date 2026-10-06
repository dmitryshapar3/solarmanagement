using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;

namespace DeyeSolar.Domain.Interfaces;

public sealed record SolarDayForecast(IReadOnlyList<SolarWeatherSample> Samples, DateTimeOffset RetrievedAt,
    DateTimeOffset? Sunrise, DateTimeOffset? Sunset, DateTimeOffset? NextSunrise);
public interface ISolarDayForecastSource
{
    Task<SolarDayForecast> ReadAsync(SolarEstimateOptions options, DateTimeOffset start, DateTimeOffset end,
        DateOnly selectedDate, CancellationToken ct);
}
