namespace DeyeSolar.Web.Operations;

public sealed class ResponseSecurityHeaders(RequestDelegate next, IHostEnvironment environment)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "same-origin";
            context.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'none'; object-src 'none'; base-uri 'self'";
            if (!environment.IsDevelopment() && context.Request.IsHttps)
                context.Response.Headers.StrictTransportSecurity = "max-age=31536000";
            return Task.CompletedTask;
        });
        return next(context);
    }
}
