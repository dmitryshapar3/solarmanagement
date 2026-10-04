using System.Security.Claims;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Integrations;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Billing;

public interface ITrialSocketQuota
{
    Task LockSocketSelectionAccountAsync(DeyeSolarDbContext db, ClaimsPrincipal actor, string installationId, CancellationToken ct);
    Task EnsureSocketSelectionAsync(DeyeSolarDbContext db, ClaimsPrincipal actor, string installationId, TrialSocketSelection selection, CancellationToken ct);
}

// Transaction-bound quota checks use the caller's context and lock order.
public sealed class TrialSocketQuota(BillingAccessService billing) : ITrialSocketQuota
{
    // The account lock precedes the integration lock and lasts through device insertion.
    public async Task LockSocketSelectionAccountAsync(DeyeSolarDbContext db, ClaimsPrincipal actor,
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
    public async Task EnsureSocketSelectionAsync(DeyeSolarDbContext db, ClaimsPrincipal actor,
        string installationId, TrialSocketSelection selection, CancellationToken ct)
    {
        var userId = SocketSelectionUser(db, actor, installationId);
        var access = await billing.ReadAsync(db, userId, ct);
        if (!access.HasAccess) throw new BillingAccessException();
        if (await db.IntegrationDeviceBindings.AnyAsync(b => b.InstanceId == selection.InstanceId
            && b.Kind == "socket" && b.RemoteId == selection.RemoteId && b.Channel == selection.Channel, ct)) return;
        if (access.SocketLimit is null) return;
        if (await db.IntegrationDeviceBindings.IgnoreQueryFilters().CountAsync(b => b.Kind == "socket"
            && b.AddedByUserId == userId, ct) >= access.SocketLimit)
            throw new IntegrationRequestException("trial_socket_limit", "The trial allows one socket. Subscribe to add more sockets.", 402);
    }

    private static string SocketSelectionUser(DeyeSolarDbContext db, ClaimsPrincipal actor, string installationId)
    {
        var userId = actor.Identity?.IsAuthenticated == true ? actor.FindFirstValue(ClaimTypes.NameIdentifier) : null;
        if (userId is null || db.InstallationId != installationId) throw new BillingAccessException();
        return userId;
    }
}
