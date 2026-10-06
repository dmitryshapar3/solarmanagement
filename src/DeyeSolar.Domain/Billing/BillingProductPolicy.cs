namespace DeyeSolar.Domain.Billing;

public sealed record BillingProductPolicy(bool Enabled, string Environment, IReadOnlyCollection<string> ProductIds)
{
    public IReadOnlyDictionary<string, string> ProductPeriods { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal)
    { ["com.dshapar.solar.monthly"] = "month", ["com.dshapar.solar.yearly"] = "year" };
}
