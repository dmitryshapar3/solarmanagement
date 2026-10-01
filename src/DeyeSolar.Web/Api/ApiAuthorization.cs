using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using DeyeSolar.Web.Data;

namespace DeyeSolar.Web.Api;

internal static class ApiAuthorization
{
    public static readonly AuthorizationPolicy BearerUser = new AuthorizationPolicyBuilder(MobileBearerAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser().RequireClaim(InstallationIds.ClaimType).Build();
    public static readonly AuthorizationPolicy AuthenticatedUser = new AuthorizationPolicyBuilder(
        IdentityConstants.ApplicationScheme, MobileBearerAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser().RequireClaim(InstallationIds.ClaimType).Build();
}
