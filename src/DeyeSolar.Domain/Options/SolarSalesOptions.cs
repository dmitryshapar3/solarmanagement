namespace DeyeSolar.Domain.Options;

public sealed class SolarSalesOptions
{
    public const string Section = "SolarSales";
    public DateOnly ContractStartDate { get; set; } = new(2026, 9, 28);
    public string TimeZoneId { get; set; } = "Europe/Warsaw";
    // The supplied TAURON annex floors negative prices unless a subsequent amendment changes this rule.
    public bool PayNegativePrices { get; set; }

    // PSE remains the default for existing installations and older clients.
    public string PriceSource { get; set; } = "pse";
    public decimal ManualPricePlnPerKwh { get; set; }
    public string PriceFeedUrl { get; set; } = "";

    public void Validate()
    {
        if (ContractStartDate < new DateOnly(2000, 1, 1) || string.IsNullOrWhiteSpace(TimeZoneId))
            throw new ArgumentException("Invalid electricity sales configuration.");
        _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        if (PriceSource is not ("pse" or "manual" or "feed")
            || ManualPricePlnPerKwh is < 0 or > 1000 || decimal.Round(ManualPricePlnPerKwh, 6) != ManualPricePlnPerKwh
            || PriceFeedUrl is null || PriceFeedUrl.Length > 2048 || PriceFeedUrl.Any(char.IsControl)
            || PriceSource == "feed" && !IsAllowedFeedUrl(PriceFeedUrl))
            throw new ArgumentException("Choose a valid sale price from 0 to 1000 PLN/kWh or a public HTTPS CSV/XML URL.");
    }
    public static bool IsAllowedFeedUrl(string? value) => value is { Length: <= 2048 } && !value.Any(char.IsControl) && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 && uri.UserInfo.Length == 0
        && uri.Fragment.Length == 0 && uri.HostNameType == UriHostNameType.Dns
        && uri.Host.Contains('.') && !uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
        && !uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
}
