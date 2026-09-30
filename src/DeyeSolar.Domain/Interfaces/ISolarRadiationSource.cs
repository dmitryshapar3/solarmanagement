using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;

namespace DeyeSolar.Domain.Interfaces;

public interface ISolarRadiationSource
{
    Task<SolarRadiationObservation> ReadAsync(SolarEstimateOptions options, DateTimeOffset now, CancellationToken ct);
}
