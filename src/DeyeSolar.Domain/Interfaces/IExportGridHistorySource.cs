using DeyeSolar.Domain.Models;

namespace DeyeSolar.Domain.Interfaces;

public interface IExportGridHistorySource
{
    /// <summary>Read signed grid watts (positive import, negative export) in a bounded UTC interval.</summary>
    Task<IReadOnlyList<ExportGridSample>> ReadAsync(string deviceSn, DateTimeOffset start,
        DateTimeOffset end, CancellationToken ct);
}
