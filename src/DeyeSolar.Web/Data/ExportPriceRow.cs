using DeyeSolar.Domain.Models;

namespace DeyeSolar.Web.Data;

public sealed class ExportPriceRow
{
    public DateTime StartUtc { get; set; }
    public decimal PricePlnPerMwh { get; set; }
    public DateTime RetrievedAtUtc { get; set; }
}

// Private feed prices never replace the shared official market publication.
public sealed class ExportFeedPriceRow : IInstallationOwned
{
    public string InstallationId { get; set; } = "";
    public string SourceKey { get; set; } = "";
    public DateTime StartUtc { get; set; }
    public decimal PricePlnPerMwh { get; set; }
    public DateTime RetrievedAtUtc { get; set; }
}
