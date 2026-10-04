namespace DeyeSolar.Domain.Billing;

public sealed record BillingProductPolicy(bool Enabled, string Environment, IReadOnlyCollection<string> ProductIds);
