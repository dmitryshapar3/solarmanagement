using System.Text.Json;
using DeyeSolar.Web.Operations;
using DeyeSolar.Web.Integrations;
using DeyeSolar.Web.Billing;
using DeyeSolar.Web.Data;
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

    [Theory]
    [InlineData("apple-mismatch", 409, "apple_account_mismatch")]
    [InlineData("apple-retry", 503, "apple_unavailable")]
    [InlineData("rule-version-missing", 428, "rule_precondition_required")]
    [InlineData("rule-version-stale", 409, "rule_configuration_conflict")]
    [InlineData("integration-conflict", 409, "configuration_conflict")]
    public async Task EndpointsAndUncaughtTypedErrorsUseOneStatusAndPublicPayload(string failure, int status, string code)
    {
        Exception error = failure switch
        {
            "apple-mismatch" => new AppleBillingException("The purchase belongs to another account.", "apple_account_mismatch"),
            "apple-retry" => new AppleBillingException("Please retry verification.", "apple_unavailable", true),
            "rule-version-missing" => new RuleConfigurationPreconditionRequiredException(),
            "rule-version-stale" => new RuleConfigurationConflictException(),
            _ => new IntegrationRequestException("configuration_conflict", "Reload before saving.", 409)
        };
        var endpoint = Context();
        await ApiProblems.Describe(error).ExecuteAsync(endpoint);
        var middleware = Context();
        await new ApiExceptionMiddleware(_ => throw error, NullLogger<ApiExceptionMiddleware>.Instance).InvokeAsync(middleware);
        foreach (var context in new[] { endpoint, middleware })
        {
            var problem = await ReadAsync(context);
            Assert.Equal(status, context.Response.StatusCode);
            Assert.Equal(code, problem.GetProperty("code").GetString());
            Assert.Equal(error.Message, problem.GetProperty("message").GetString());
            Assert.Equal(error.Message, problem.GetProperty("detail").GetString());
            Assert.Equal("no-store", context.Response.Headers.CacheControl);
            Assert.Equal(status == 503 ? "30" : "", context.Response.Headers.RetryAfter.ToString());
        }
    }

    [Fact]
    public void KnownValidationIsPublicOnlyWithinTheOwningApiScope()
    {
        var error = new ArgumentException("Private lower-level operation details");
        Assert.Equal(400, ApiProblems.Describe(error, ApiProblemScope.Rules).Status);
        Assert.Equal(400, ApiProblems.Describe(error, ApiProblemScope.Integrations).Status);
        Assert.Equal(500, ApiProblems.Describe(error).Status);
        Assert.DoesNotContain("Private", ApiProblems.Describe(error).Message);
        var billing = new BillingAccessException("Account access changed.");
        Assert.Equal(409, ApiProblems.Describe(billing, ApiProblemScope.Billing).Status);
        Assert.Equal(402, ApiProblems.Describe(billing, ApiProblemScope.Integrations).Status);
        Assert.Equal("subscription_required", ApiProblems.Describe(billing, ApiProblemScope.Integrations).Code);
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
