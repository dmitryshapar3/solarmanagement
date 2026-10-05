using System.Security.Claims;
using DeyeSolar.Web.Data;
using Microsoft.EntityFrameworkCore;
namespace DeyeSolar.Web.Auth;
public enum InstallationPermission { Read, ManageRules, ControlDevices, ManageSettings, ManageIntegrations }
public sealed class InstallationAccessException(string message, int status = 403) : Exception(message)
{
    public int Status { get; } = status;
}
public interface IInstallationAccessAuthorizer
{
    Task<InstallationMembership> CheckAsync(ClaimsPrincipal actor, string installationId, InstallationPermission permission, CancellationToken ct = default);
}
public sealed class InstallationAccessAuthorizer(DbContextOptions<DeyeSolarDbContext> database, IAccountSessionStore sessions,
    TimeProvider clock) : IInstallationAccessAuthorizer
{
    public const string SessionClaim = "solar:session";
    public const string StampClaim = "AspNet.Identity.SecurityStamp";
    public async Task<InstallationMembership> CheckAsync(ClaimsPrincipal actor, string installationId,
        InstallationPermission permission, CancellationToken ct = default)
    {
        var userId = actor.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actor.Identity?.IsAuthenticated != true || userId is null) throw Denied(401);
        await using var db = new DeyeSolarDbContext(database);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) throw Denied(401);
        var stamp = actor.FindFirstValue(StampClaim);
        // Real cookie and bearer sessions must carry a stamp. Synthetic test schemes are not production identities.
        if (!AccountSessionValidator.IsActiveUser(user, stamp, clock.GetUtcNow())) throw Denied(401);
        var token = actor.FindFirstValue(SessionClaim);
        var session = token is null ? null : await sessions.FindAsync(token, ct);
        if (!AccountSessionValidator.MatchesInstallation(session, userId, user.SecurityStamp, installationId)) throw Denied(401);
        var membership = await db.InstallationMemberships.AsNoTracking().Include(m => m.Installation)
            .SingleOrDefaultAsync(m => m.UserId == userId && m.InstallationId == installationId && m.Installation.IsEnabled, ct);
        if (membership is null || !Allows(membership.Role, permission)) throw Denied(403);
        return membership;
    }
    public static bool Allows(string role, InstallationPermission permission)
        => InstallationPermissionPolicy.Allows(role, permission);
    private static InstallationAccessException Denied(int status) => new("Your access has changed. Sign in again or contact the installation owner.", status);
}
// Bound once per request/circuit. Stored claims are rechecked in SQL on every application operation.
public sealed class InteractiveSecurityContext(IInstallationAccessAuthorizer authorizer, CurrentInstallation installation,
    IHttpContextAccessor http)
{
    private ClaimsPrincipal? _actor;
    public void BindOnce(ClaimsPrincipal actor)
    {
        if (_actor is not null && _actor.FindFirstValue(ClaimTypes.NameIdentifier) != actor.FindFirstValue(ClaimTypes.NameIdentifier))
            throw new InstallationAccessException("This circuit already belongs to another account.");
        _actor ??= actor;
    }
    public Task<InstallationMembership> EnsureAsync(InstallationPermission permission, CancellationToken ct = default)
    {
        var actor = _actor ?? http.HttpContext?.User ?? throw new InstallationAccessException("Sign in again.", 401);
        return authorizer.CheckAsync(actor, installation.Id ?? throw new InstallationAccessException("No installation is available."), permission, ct);
    }
}
