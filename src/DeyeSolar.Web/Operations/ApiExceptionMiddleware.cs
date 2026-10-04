using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Billing;
using DeyeSolar.Web.Integrations;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

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
            var problem = Describe(exception);
            if (problem.Status >= 500) logger.LogError(exception, "API operation failed; trace {TraceId}", context.TraceIdentifier);
            context.Response.Clear();
            context.Response.StatusCode = problem.Status;
            context.Response.Headers.CacheControl = "no-store";
            if (problem.Status is 429 or 503) context.Response.Headers.RetryAfter = "30";
            await context.Response.WriteAsJsonAsync(new
            {
                type = "about:blank", title = problem.Title, status = problem.Status,
                detail = problem.Message, message = problem.Message, code = problem.Code,
                instance = context.Request.Path.Value, traceId = context.TraceIdentifier
            }, options: null, contentType: "application/problem+json", cancellationToken: context.RequestAborted);
        }
    }

    private static ApiProblem Describe(Exception exception) => exception switch
    {
        DeyeSolar.Web.Data.RuleConfigurationPreconditionRequiredException e => new(428, "Precondition required", "rule_precondition_required", e.Message),
        DeyeSolar.Web.Data.RuleConfigurationConflictException e => new(409, "Update conflict", "rule_configuration_conflict", e.Message),
        BadHttpRequestException e => new(e.StatusCode, "Invalid request", "invalid_request", "The request body is invalid or exceeds the allowed size."),
        Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException => new(400, "Invalid request", "antiforgery", "A valid request verification token is required."),
        IntegrationRequestException e => new(e.Status, "Integration operation failed", e.Code, e.Message),
        InstallationAccessException e => new(e.Status, "Access changed", "installation_access_denied", e.Message),
        AccountSecurityException e => new(e.Status, "Account operation failed", e.Code, e.Message),
        BillingAccessException e => new(402, "Subscription required", "billing_required", e.Message),
        AppleBillingException e => new(e.Retryable ? 503 : 400, "Subscription verification failed", e.Code, e.Message),
        DbUpdateConcurrencyException => new(409, "Update conflict", "update_conflict", "The data changed. Reload it before continuing."),
        SqlException => new(503, "Service unavailable", "database_unavailable", "The service is temporarily unavailable. Please try again shortly."),
        DbUpdateException { InnerException: SqlException { Number: 2601 or 2627 } } => new(409, "Update conflict", "duplicate_record", "The data changed. Reload it before continuing."),
        DbUpdateException { InnerException: SqlException } => new(503, "Service unavailable", "database_unavailable", "The service is temporarily unavailable. Please try again shortly."),
        SolarManagement.Integrations.Contracts.IntegrationOperationException e => new(e.IsTransient ? 503 : 502,
            "Integration unavailable", "integration_unavailable", "The integration operation is unavailable."),
        _ => new(500, "Server error", "internal_error", "The request could not be completed. Please try again later.")
    };
    private sealed record ApiProblem(int Status, string Title, string Code, string Message);
}
