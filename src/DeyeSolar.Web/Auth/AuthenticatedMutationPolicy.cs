using DeyeSolar.Web.Api;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace DeyeSolar.Web.Auth;

/// <summary>Ambient cookies require CSRF even when the same request also supplies a bearer.</summary>
public static class AuthenticatedMutationPolicy
{
    public static bool RequiresAntiforgery(bool bearerAuthenticated, bool cookieAuthenticated)
        => cookieAuthenticated || !bearerAuthenticated;

    public static async Task EnsureAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (context.Items.ContainsKey(typeof(AuthenticatedMutationPolicy))) return;
        var bearer = await context.AuthenticateAsync(MobileBearerAuthenticationHandler.SchemeName);
        var cookie = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (RequiresAntiforgery(bearer.Succeeded, cookie.Succeeded)) await antiforgery.ValidateRequestAsync(context);
        context.Items[typeof(AuthenticatedMutationPolicy)] = true;
    }

    public static async Task<bool> IsAllowedAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try { await EnsureAsync(context, antiforgery); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }
}
