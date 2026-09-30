namespace DeyeSolar.Domain.Options;

public sealed class SolarSalesOptions
{
    public const string Section = "SolarSales";
    public DateOnly ContractStartDate { get; set; } = new(2026, 9, 28);
    public string TimeZoneId { get; set; } = "Europe/Warsaw";
    // The supplied TAURON annex floors negative prices unless a subsequent amendment changes this rule.
    public bool PayNegativePrices { get; set; }

    public void Validate()
    {
        if (ContractStartDate < new DateOnly(2000, 1, 1) || string.IsNullOrWhiteSpace(TimeZoneId))
            throw new ArgumentException("Invalid electricity sales configuration.");
        _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
    }
}
