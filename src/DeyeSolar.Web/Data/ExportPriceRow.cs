namespace DeyeSolar.Web.Data;

public sealed class ExportPriceRow
{
    public DateTime StartUtc { get; set; }
    public decimal PricePlnPerMwh { get; set; }
    public DateTime RetrievedAtUtc { get; set; }
}
