namespace DeyeSolar.Web.Operations;

/// <summary>Public bookmarks retain query parameters; API and POST contracts never pass through this mapping.</summary>
public sealed class LegacyUiRedirects(RequestDelegate next, IHostEnvironment environment)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!environment.IsDevelopment() && context.Request.Path.StartsWithSegments("/_design"))
        { context.Response.StatusCode = 404; return; }
        if (context.Request.Method is "GET" or "HEAD" && Destination(context.Request.Path.Value ?? "") is { } target)
        {
            context.Response.Redirect(target + context.Request.QueryString, permanent: true);
            return;
        }
        await next(context);
    }
    public static string? Destination(string path)
    {
        if (path.StartsWith("/rules/edit/", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(path[12..], out var id) && id > 0) return $"/automations/{id}";
        return path.TrimEnd('/').ToLowerInvariant() switch
        {
            "/generation" or "/solar-details" => "/energy",
            "/sales" or "/sales-details" => "/energy/export",
            "/rules" => "/automations", "/rules/edit" => "/automations/new",
            "/runs" => "/activity", "/history" or "/inverter-details" => "/activity/readings",
            "/account" or "/billing" => "/settings/account",
            "/login" or "/register" or "/verify" => "/signin", _ => null
        };
    }
}
