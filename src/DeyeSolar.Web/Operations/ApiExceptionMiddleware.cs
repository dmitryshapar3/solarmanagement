namespace DeyeSolar.Web.Operations;

/// <summary>Preserves JSON contracts even when an operation fails before an endpoint can respond.</summary>
public sealed class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api")) { await next(context); return; }
        // API payloads contain configuration and receipts; provider archives are fetched by the package manager.
        var bodyLimit = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = 1024 * 1024;
        context.Response.Headers.CacheControl = "no-store";
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The peer has gone away; writing a new response would misreport cancellation as a server error.
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            var problem = ApiProblems.Describe(exception);
            if (problem.Status >= 500) logger.LogError(exception, "API operation failed; trace {TraceId}", context.TraceIdentifier);
            context.Response.Clear();
            await problem.ExecuteAsync(context);
        }
    }

}
