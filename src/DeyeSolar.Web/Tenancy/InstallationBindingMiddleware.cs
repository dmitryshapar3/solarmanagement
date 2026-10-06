using DeyeSolar.Web.Data;

namespace DeyeSolar.Web.Tenancy;

public sealed class InstallationBindingMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, CurrentInstallation current,
        InstallationMembershipService memberships, TenantRuntimeRegistry runtimes,
        ILogger<InstallationBindingMiddleware> logger)
    {
        // Auth endpoints resolve identities without reading installation data.
        // Cookie and bearer principals both pass the same database membership check.
        if (context.User.Identity?.IsAuthenticated == true)
        {
            if (IsIdentityPath(context.Request.Path) || Operations.UiRoutePolicy.IsDevelopmentGallery(context))
            {
                await next(context);
                return;
            }
            var membership = await memberships.ResolveAsync(context.User, context.RequestAborted);
            if (membership is null)
            {
                if (context.Request.Path.StartsWithSegments("/api") && !context.Request.Path.StartsWithSegments("/api/auth"))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new { message = "No active installation is available for this account." }, context.RequestAborted);
                    return;
                }
            }
            else
            {
                current.BindOnce(membership.InstallationId);
                try { await runtimes.GetAsync(membership.InstallationId, context.RequestAborted); }
                catch (Exception exception) when (!context.RequestAborted.IsCancellationRequested)
                {
                    logger.LogWarning("Installation initialization is unavailable ({ErrorType})", exception.GetType().Name);
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    context.Response.Headers.RetryAfter = "30";
                    const string message = "Your installation is temporarily unavailable. Please try again shortly.";
                    if (context.Request.Path.StartsWithSegments("/api"))
                        await context.Response.WriteAsJsonAsync(new { message }, context.RequestAborted);
                    else await context.Response.WriteAsync(message, context.RequestAborted);
                    return;
                }
            }
        }
        await next(context);
    }

    private static bool IsIdentityPath(PathString path) => path.StartsWithSegments("/api/auth")
        || Operations.UiRoutePolicy.IsAccount(path)
        || path.StartsWithSegments("/api/billing") || path == "/billing"
        || path.StartsWithSegments("/auth") || path == "/account" || path == "/account/language" || path == "/api/account/language"
        || path == "/login"
        || path == "/logout" || path == "/register" || path == "/verify";
}
