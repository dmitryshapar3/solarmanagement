using System.Security.Claims;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Integrations;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Billing;

public sealed class BillingAccessService(DbContextOptions<DeyeSolarDbContext> options, TimeProvider clock,
    AppleBillingOptions apple)
{
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
        var cacheUntil = subscription.CheckedAt + AppleBillingOptions.MaximumStatusAge;
        if (paidUntil is not { } end || end <= now || cacheUntil <= now) return null;
        return end < cacheUntil ? end : cacheUntil;
    }

    public async Task<BillingAccess> ReadAsync(string userId, CancellationToken ct = default)
    {
        await using var db = new DeyeSolarDbContext(options);
        return await ReadAsync(db, userId, ct);
    }

    private async Task<BillingAccess> ReadAsync(DeyeSolarDbContext db, string userId, CancellationToken ct)
    {
        var account = await db.BillingAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.UserId == userId, ct)
            ?? throw new BillingAccessException("This account has no billing record. Contact support.");
        var subscriptions = await db.AppleSubscriptions.AsNoTracking().Where(s => s.UserId == userId).ToListAsync(ct);
        return ResolveAccess(account, subscriptions, clock.GetUtcNow());
    }

    private BillingAccess ResolveAccess(BillingAccount account, IEnumerable<AppleSubscription> subscriptions, DateTimeOffset now)
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

    public async Task EnsureUserAsync(ClaimsPrincipal actor, CancellationToken ct)
    {
        var userId = actor.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actor.Identity?.IsAuthenticated != true || userId is null || !(await ReadAsync(userId, ct)).HasAccess)
            throw new BillingAccessException();
    }

    public async Task<bool> InstallationHasAccessAsync(string installationId, CancellationToken ct)
    {
        await using var db = new DeyeSolarDbContext(options);
        var members = db.InstallationMemberships
            .Where(m => m.InstallationId == installationId && m.Installation.IsEnabled).Select(m => m.UserId);
        var accounts = await db.BillingAccounts.AsNoTracking().Where(a => members.Contains(a.UserId)).ToListAsync(ct);
        if (accounts.Count == 0) return false;
        var accountIds = accounts.Select(a => a.UserId).ToList();
        var subscriptions = (await db.AppleSubscriptions.AsNoTracking().Where(s => accountIds.Contains(s.UserId)).ToListAsync(ct))
            .ToLookup(s => s.UserId);
        var now = clock.GetUtcNow();
        return accounts.Any(account => ResolveAccess(account, subscriptions[account.UserId], now).HasAccess);
    }

    public async Task EnsureInstallationAsync(string installationId, CancellationToken ct)
    {
        if (!await InstallationHasAccessAsync(installationId, ct)) throw new BillingAccessException();
    }

    // The account lock precedes the integration lock and lasts through device insertion.
    internal async Task LockSocketSelectionAccountAsync(DeyeSolarDbContext db, ClaimsPrincipal actor,
        string installationId, CancellationToken ct)
    {
        var userId = SocketSelectionUser(db, actor, installationId);
        var account = db.Database.IsSqlServer()
            ? await db.BillingAccounts.FromSqlInterpolated($"SELECT * FROM [BillingAccounts] WITH (UPDLOCK, HOLDLOCK) WHERE [UserId] = {userId}")
                .AsNoTracking().SingleOrDefaultAsync(ct)
            : await db.BillingAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.UserId == userId, ct);
        if (account is null) throw new BillingAccessException();
    }

    // Check eligibility after acquiring the integration lock: waiting may cross an access deadline.
    // A paying member's quota is independent of another member's expired trial.
    internal async Task EnsureSocketSelectionAsync(DeyeSolarDbContext db, ClaimsPrincipal actor,
        string installationId, TrialSocketSelection selection, CancellationToken ct)
    {
        var userId = SocketSelectionUser(db, actor, installationId);
        var access = await ReadAsync(db, userId, ct);
        if (!access.HasAccess) throw new BillingAccessException();
        if (await db.IntegrationDeviceBindings.AnyAsync(b => b.InstanceId == selection.InstanceId
            && b.Kind == "socket" && b.RemoteId == selection.RemoteId && b.Channel == selection.Channel, ct)) return;
        if (access.SocketLimit is null) return;
        var installations = db.InstallationMemberships.Where(m => m.UserId == userId).Select(m => m.InstallationId);
        if (await db.IntegrationDeviceBindings.IgnoreQueryFilters().CountAsync(b => b.Kind == "socket"
            && (b.AddedByUserId == userId || b.AddedByUserId == null && installations.Contains(b.InstallationId)), ct) >= access.SocketLimit)
            throw new IntegrationRequestException("trial_socket_limit", "The trial allows one socket. Subscribe to add more sockets.", 402);
    }

    private static string SocketSelectionUser(DeyeSolarDbContext db, ClaimsPrincipal actor, string installationId)
    {
        var userId = actor.Identity?.IsAuthenticated == true ? actor.FindFirstValue(ClaimTypes.NameIdentifier) : null;
        if (userId is null || db.InstallationId != installationId) throw new BillingAccessException();
        return userId;
    }
}

internal sealed record TrialSocketSelection(Guid InstanceId, string RemoteId, string Channel);

public sealed class BillingAccessException(string message = "Your trial has ended. Subscribe to read or control your sockets.")
    : InvalidOperationException(message);
