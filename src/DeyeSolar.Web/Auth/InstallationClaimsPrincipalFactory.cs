using System.Security.Claims;
using DeyeSolar.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Web.Auth;

public sealed class InstallationClaimsPrincipalFactory(UserManager<IdentityUser> users, RoleManager<IdentityRole> roles,
    IOptions<IdentityOptions> options, InstallationMembershipService memberships)
    : UserClaimsPrincipalFactory<IdentityUser, IdentityRole>(users, roles, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(IdentityUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        var membership = await memberships.GetForUserAsync(user.Id);
        if (membership is not null)
        {
            identity.AddClaim(new(InstallationIds.ClaimType, membership.InstallationId));
            identity.AddClaim(new(InstallationIds.RoleClaimType, membership.Role));
        }
        return identity;
    }
}
