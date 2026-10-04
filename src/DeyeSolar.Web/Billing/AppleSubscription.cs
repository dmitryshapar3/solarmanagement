namespace DeyeSolar.Web.Billing;

public sealed class AppleSubscription
{
    public string OriginalTransactionId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public Guid AppAccountToken { get; set; }
    public string ProductId { get; set; } = string.Empty;
    public string TransactionId { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public AppleSubscriptionStatus Status { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? GracePeriodExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? InvalidatedAt { get; set; }
    public bool IsFreeTrial { get; set; }
    public DateTimeOffset SourceSignedAt { get; set; }
    public DateTimeOffset CheckedAt { get; set; }
    // A slower response from an earlier refresh cannot replace a later observation.
    public DateTimeOffset ObservationStartedAt { get; set; }
}

public enum AppleSubscriptionStatus
{
    Active = 1,
    Expired = 2,
    BillingRetry = 3,
    BillingGracePeriod = 4,
    Revoked = 5
}
