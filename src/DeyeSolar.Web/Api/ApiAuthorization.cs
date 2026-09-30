using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;

namespace DeyeSolar.Web.Api;

internal static class ApiAuthorization
{
    public static readonly AuthorizationPolicy AuthenticatedUser = new AuthorizationPolicyBuilder(
        IdentityConstants.ApplicationScheme, MobileBearerAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser().Build();
}
