using System.Security.Claims;
using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Data;
namespace DeyeSolar.Web.Tests;
// Synthetic domain fixture actors have no cookie/bearer token. Production authorizer has
// separate SQL/browser tests; this adapter retains real membership and permission checks.
internal sealed class FixtureInstallationAuthorizer(InstallationMembershipService memberships) : IInstallationAccessAuthorizer
{
    public async Task<InstallationMembership> CheckAsync(ClaimsPrincipal actor, string installationId, InstallationPermission permission, CancellationToken ct = default)
    {
        var member = await memberships.ResolveAsync(actor, ct);
        if (member is null || member.InstallationId != installationId || !InstallationAccessAuthorizer.Allows(member.Role, permission))
            throw new InstallationAccessException("Access is denied.");
        return member;
    }
}
