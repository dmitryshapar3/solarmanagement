namespace DeyeSolar.Domain.Billing;

// Pure time and product policy; no database, transport or request state.
public static class BillingEntitlementPolicy
{
    public static TimeSpan MaximumStatusAge => TimeSpan.FromHours(1);
    public static bool HasPaidAccess(AppleSubscription subscription, DateTimeOffset now)
        => PaidAccessValidUntil(subscription, now) is not null;

    public static DateTimeOffset? PaidAccessValidUntil(AppleSubscription subscription, DateTimeOffset now)
    {
        if (subscription.IsFreeTrial || subscription.InvalidatedAt is not null || subscription.RevokedAt is not null
            || subscription.CheckedAt > now) return null;
        var paidUntil = subscription.Status switch
        {
            AppleSubscriptionStatus.Active => subscription.ExpiresAt,
            AppleSubscriptionStatus.BillingGracePeriod => subscription.GracePeriodExpiresAt,
            _ => null
        };
        var cacheUntil = subscription.CheckedAt + MaximumStatusAge;
        if (paidUntil is not { } end || end <= now || cacheUntil <= now) return null;
        return end < cacheUntil ? end : cacheUntil;
    }

    public static BillingAccess ResolveAccess(BillingAccount account, IEnumerable<AppleSubscription> subscriptions, DateTimeOffset now, BillingProductPolicy apple)
    {
        var active = subscriptions.Where(s => apple.Enabled && s.AppAccountToken == account.AppAccountToken
                && s.Environment == apple.Environment && apple.ProductIds.Contains(s.ProductId, StringComparer.Ordinal))
            .Select(s => new { Subscription = s, ValidUntil = PaidAccessValidUntil(s, now) })
            .Where(s => s.ValidUntil is not null).OrderByDescending(s => s.ValidUntil).FirstOrDefault();
        var trial = now >= account.TrialStartedAt && now < account.TrialEndsAt;
        var accessValidUntil = active?.ValidUntil;
        if (trial && (accessValidUntil is null || account.TrialEndsAt > accessValidUntil)) accessValidUntil = account.TrialEndsAt;
        return new(active is not null ? "active" : trial ? "trial" : "expired", active is not null || trial,
            account.TrialEndsAt, active is null ? null : active.Subscription.Status == AppleSubscriptionStatus.BillingGracePeriod
                ? active.Subscription.GracePeriodExpiresAt : active.Subscription.ExpiresAt,
            account.AppAccountToken, active is not null ? null : 1, apple.Enabled, now, accessValidUntil);
    }

}
