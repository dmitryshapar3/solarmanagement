using System.Security.Claims;

namespace DeyeSolar.Web.Billing;

public sealed class BillingAccessMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, BillingAccessService billing, CurrentBillingAccount account)
    {
        if (context.User.Identity?.IsAuthenticated == true && context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { } userId)
            account.BindOnce(userId);
        if (context.User.Identity?.IsAuthenticated == true && !IsAccountPath(context.Request.Path))
        {
            try { await billing.EnsureUserAsync(context.User, context.RequestAborted); }
            catch (BillingAccessException error)
            {
                context.Response.Headers.CacheControl = "no-store";
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = StatusCodes.Status402PaymentRequired;
                    await context.Response.WriteAsJsonAsync(new { code = "subscription_required", message = error.Message }, context.RequestAborted);
                }
                else context.Response.Redirect("/billing");
                return;
            }
        }
        await next(context);
    }

    private static bool IsAccountPath(PathString path) => path.StartsWithSegments("/api/auth")
        || path.StartsWithSegments("/api/billing") || path.StartsWithSegments("/auth")
        || path == "/billing" || path == "/account" || path == "/account/language" || path == "/api/account/language"
        || path == "/login" || path == "/logout"
        || path == "/register" || path == "/verify" || path == "/privacy" || path == "/support"
        || path == "/terms" || path == "/Error";
}
