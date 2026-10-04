using System.Text.Json;
using DeyeSolar.Web.Operations;
using DeyeSolar.Web.Integrations;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeyeSolar.Web.Tests;

public sealed class ApiBoundaryTests
{
    [Fact]
    public async Task UnexpectedApiFailureReturnsSafeJsonWithTraceAndNeverAnHtmlErrorPage()
    {
        var context = Context();
        var middleware = new ApiExceptionMiddleware(_ => throw new InvalidOperationException("secret:connection-password"),
            NullLogger<ApiExceptionMiddleware>.Instance);
        await middleware.InvokeAsync(context);
        var problem = await ReadAsync(context);
        Assert.Equal(500, context.Response.StatusCode);
        Assert.StartsWith("application/problem+json", context.Response.ContentType);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Equal(context.TraceIdentifier, problem.GetProperty("traceId").GetString());
        Assert.DoesNotContain("secret", problem.ToString());
    }

    [Fact]
    public async Task TypedMutationConflictKeepsStatusAndMachineReadableCode()
    {
        var context = Context();
        var middleware = new ApiExceptionMiddleware(_ => throw new IntegrationRequestException("configuration_conflict", "Reload before saving.", 409),
            NullLogger<ApiExceptionMiddleware>.Instance);
        await middleware.InvokeAsync(context);
        var problem = await ReadAsync(context);
        Assert.Equal(409, context.Response.StatusCode);
        Assert.Equal("configuration_conflict", problem.GetProperty("code").GetString());
        Assert.Equal("Reload before saving.", problem.GetProperty("message").GetString());
    }

    [Fact]
    public async Task InvalidAntiforgeryTokenReturnsAControlledClientErrorWithoutLeakingDiagnostics()
    {
        var context = Context();
        var middleware = new ApiExceptionMiddleware(_ => throw new Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException("private cookie validation details"),
            NullLogger<ApiExceptionMiddleware>.Instance);
        await middleware.InvokeAsync(context);
        var problem = await ReadAsync(context);
        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal("antiforgery", problem.GetProperty("code").GetString());
        Assert.Equal("A valid request verification token is required.", problem.GetProperty("message").GetString());
        Assert.DoesNotContain("private", problem.ToString());
    }

    [Fact]
    public async Task DisconnectedCallerDoesNotBecomeAnInternalError()
    {
        var context = Context();
        context.RequestAborted = new CancellationToken(canceled: true);
        await new ApiExceptionMiddleware(_ => throw new OperationCanceledException(context.RequestAborted),
            NullLogger<ApiExceptionMiddleware>.Instance).InvokeAsync(context);
        Assert.Equal(0, context.Response.Body.Length);
    }

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/devices";
        context.Response.Body = new MemoryStream();
        return context;
    }
    private static async Task<JsonElement> ReadAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.Clone();
    }
}
