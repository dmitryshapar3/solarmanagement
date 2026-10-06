namespace DeyeSolar.Domain.Billing;

public sealed class BillingAccount
{
    public string UserId { get; set; } = string.Empty;
    public Guid AppAccountToken { get; set; }
    public DateTimeOffset TrialStartedAt { get; set; }
    public DateTimeOffset TrialEndsAt => TrialStartedAt.AddMonths(1);

    public static BillingAccount Create(string userId, DateTimeOffset now) => new()
    {
        UserId = userId,
        AppAccountToken = Guid.NewGuid(),
        TrialStartedAt = now.ToUniversalTime()
    };
}

public sealed record BillingAccess(string Status, bool HasAccess, DateTimeOffset TrialEndsAt,
    DateTimeOffset? SubscriptionExpiresAt, Guid AppAccountToken, int? SocketLimit, bool AppleSubscriptionsEnabled,
    DateTimeOffset ServerNow, DateTimeOffset? AccessValidUntil)
{
    public string? ProductId { get; init; }
    public string? PlanPeriod { get; init; }
    public bool? AutoRenewEnabled { get; init; }
    public DateTimeOffset? RenewalAt { get; init; }
    public int TrialDaysRemaining { get; init; }
    public int SocketUsage { get; init; }
}
