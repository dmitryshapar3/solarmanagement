using System.Security.Claims;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Integrations;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Billing;

public sealed class BillingAccessService(DbContextOptions<DeyeSolarDbContext> options, TimeProvider clock,
    AppleBillingOptions apple) : IBillingAccessReader
{
    public static bool HasPaidAccess(AppleSubscription subscription, DateTimeOffset now)
        => BillingEntitlementPolicy.PaidAccessValidUntil(subscription, now) is not null;
    public static DateTimeOffset? PaidAccessValidUntil(AppleSubscription subscription, DateTimeOffset now)
        => BillingEntitlementPolicy.PaidAccessValidUntil(subscription, now);

    public async Task<BillingAccess> ReadAsync(string userId, CancellationToken ct = default)
    {
        await using var db = new DeyeSolarDbContext(options);
        return await ReadAsync(db, userId, ct);
    }

    internal async Task<BillingAccess> ReadAsync(DeyeSolarDbContext db, string userId, CancellationToken ct)
    {
        var account = await db.BillingAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.UserId == userId, ct)
            ?? throw new BillingAccessException("This account has no billing record. Contact support.");
        var subscriptions = await db.AppleSubscriptions.AsNoTracking().Where(s => s.UserId == userId).ToListAsync(ct);
        return BillingEntitlementPolicy.ResolveAccess(account, subscriptions, clock.GetUtcNow(), apple.ProductPolicy);
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
        return accounts.Any(account => BillingEntitlementPolicy.ResolveAccess(account, subscriptions[account.UserId], now, apple.ProductPolicy).HasAccess);
    }

    public async Task EnsureInstallationAsync(string installationId, CancellationToken ct)
    {
        if (!await InstallationHasAccessAsync(installationId, ct)) throw new BillingAccessException();
    }


}

public sealed record TrialSocketSelection(Guid InstanceId, string RemoteId, string Channel);

public sealed class BillingAccessException(string message = "Your trial has ended. Subscribe to read or control your sockets.")
    : InvalidOperationException(message);
