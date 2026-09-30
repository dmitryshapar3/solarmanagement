using DeyeSolar.Domain.Models;

namespace DeyeSolar.Domain.Interfaces;

public interface IExportPriceSource
{
    /// <summary>
    /// Return published, signed RCE prices for complete UTC quarter-hours in [start, end).
    /// Boundaries must align to quarter-hours and the range must not exceed 367 days,
    /// accommodating 366 local calendar dates across a daylight-saving transition.
    /// Unpublished prices remain gaps; settlement rules are applied by the caller.
    /// </summary>
    Task<IReadOnlyList<ExportPriceInterval>> ReadAsync(DateTimeOffset start,
        DateTimeOffset end, CancellationToken ct);
}
