namespace DeyeSolar.Web.Operations;

internal static class UiRoutePolicy
{
    public static bool IsAccount(PathString path) => path == "/signin" || path.StartsWithSegments("/settings/account")
        || path.StartsWithSegments("/api/account");
    public static bool IsDevelopmentGallery(HttpContext context) => context.Request.Path == "/_design"
        && context.RequestServices.GetService<IHostEnvironment>()?.IsDevelopment() == true;
}
