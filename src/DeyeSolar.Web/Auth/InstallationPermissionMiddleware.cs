using DeyeSolar.Web.Data;
using Microsoft.AspNetCore.Antiforgery;
namespace DeyeSolar.Web.Auth;
public sealed record InstallationPermissionMetadata(InstallationPermission Permission);
public sealed class InstallationPermissionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, CurrentInstallation current, InteractiveSecurityContext security,
        IAntiforgery antiforgery)
    {
        if (context.User.Identity?.IsAuthenticated == true && current.Id is not null
            && !IsAccountPath(context.Request.Path) && !Operations.UiRoutePolicy.IsDevelopmentGallery(context))
        {
            security.BindOnce(context.User);
            var permission = context.GetEndpoint()?.Metadata.GetMetadata<InstallationPermissionMetadata>()?.Permission ?? InstallationPermission.Read;
            await security.EnsureAsync(permission, context.RequestAborted);
            if (context.Request.Path.StartsWithSegments("/api") && context.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE")
            {
                await AuthenticatedMutationPolicy.EnsureAsync(context, antiforgery);
            }
        }
        await next(context);
    }
    private static bool IsAccountPath(PathString path) => path.StartsWithSegments("/api/auth") || path.StartsWithSegments("/api/billing")
        || Operations.UiRoutePolicy.IsAccount(path)
        || path.StartsWithSegments("/auth") || path.StartsWithSegments("/account") || path == "/billing"
        || path == "/login" || path == "/register" || path == "/verify" || path == "/logout";
}
