namespace DeyeSolar.Web.Billing;

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
    DateTimeOffset ServerNow, DateTimeOffset? AccessValidUntil);
