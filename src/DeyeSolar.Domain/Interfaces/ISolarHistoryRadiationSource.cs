using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;

namespace DeyeSolar.Domain.Interfaces;

public interface ISolarHistoryRadiationSource
{
    /// <summary>
    /// Return completed hourly mean radiation in [start, end), with Timestamp identifying
    /// the UTC hour's start. Weather values are instantaneous at that hour's end.
    /// </summary>
    Task<IReadOnlyList<SolarWeatherSample>> ReadAsync(SolarEstimateOptions options,
        DateTimeOffset start, DateTimeOffset end, CancellationToken ct);
}
