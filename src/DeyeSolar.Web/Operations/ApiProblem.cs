using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Billing;
using DeyeSolar.Web.Integrations;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Operations;

public enum ApiProblemScope { General, Rules, Integrations, Billing }

/// <summary>One renderer preserves human messages, machine codes and response security headers.</summary>
public sealed record ApiProblem(int Status, string Title, string? Code, string Message) : IResult
{
    public async Task ExecuteAsync(HttpContext context)
    {
        context.Response.StatusCode = Status;
        context.Response.Headers.CacheControl = "no-store";
        if (Status is 429 or 503) context.Response.Headers.RetryAfter = "30";
        await context.Response.WriteAsJsonAsync(new
        {
            type = "about:blank", title = Title, status = Status, detail = Message, message = Message,
            code = Code, instance = context.Request.Path.Value, traceId = context.TraceIdentifier
        }, options: null, contentType: "application/problem+json", cancellationToken: context.RequestAborted);
    }
}

public static class ApiProblems
{
    public static ApiProblem InvalidAntiforgery() => new(400, "Invalid request", "antiforgery", "A valid request verification token is required.");
    public static ApiProblem Error(string message, int status = 400, string? code = null)
        => new(status, "Request failed", code, message);

    public static ApiProblem Describe(Exception exception, ApiProblemScope scope = ApiProblemScope.General)
        => TryDescribe(exception, scope) ?? new(500, "Server error", "internal_error", "The request could not be completed. Please try again later.");

    public static bool IsHandled(Exception exception, ApiProblemScope scope) => TryDescribe(exception, scope) is not null;
    private static ApiProblem? TryDescribe(Exception exception, ApiProblemScope scope) => exception switch
    {
        DeyeSolar.Web.Data.RuleConfigurationPreconditionRequiredException e => new(428, "Precondition required", "rule_precondition_required", e.Message),
        DeyeSolar.Web.Data.RuleConfigurationConflictException e => new(409, "Update conflict", "rule_configuration_conflict", e.Message),
        BadHttpRequestException e => new(e.StatusCode, "Invalid request", "invalid_request", "The request body is invalid or exceeds the allowed size."),
        Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException => InvalidAntiforgery(),
        IntegrationRequestException e => new(e.Status, "Integration operation failed", e.Code, e.Message),
        InstallationAccessException e => new(e.Status, "Access changed", "installation_access_denied", e.Message),
        AccountSecurityException e => new(e.Status, "Account operation failed", e.Code, e.Message),
        BillingAccessException e when scope == ApiProblemScope.Billing => new(409, "Account unavailable", "billing_account_unavailable", e.Message),
        BillingAccessException e => new(402, "Subscription required", scope == ApiProblemScope.Integrations ? "subscription_required" : "billing_required", e.Message),
        AppleBillingException e => new(e.HttpStatus, "Subscription verification failed", e.Code, e.Message),
        DbUpdateConcurrencyException => new(409, "Update conflict", "update_conflict", "The data changed. Reload it before continuing."),
        SqlException => new(503, "Service unavailable", "database_unavailable", "The service is temporarily unavailable. Please try again shortly."),
        DbUpdateException { InnerException: SqlException { Number: 2601 or 2627 } } => new(409, "Update conflict", "duplicate_record", "The data changed. Reload it before continuing."),
        DbUpdateException { InnerException: SqlException } => new(503, "Service unavailable", "database_unavailable", "The service is temporarily unavailable. Please try again shortly."),
        SolarManagement.Integrations.Contracts.IntegrationOperationException e => new(e.IsTransient ? 503 : 502,
            "Integration unavailable", "integration_unavailable", "The integration operation is unavailable."),
        VerificationRateLimitException => new(429, "Too many requests", "verification_limit", "Please wait before requesting another verification code."),
        KeyNotFoundException when scope == ApiProblemScope.Integrations => new(404, "Not found", "provider_not_found", "The requested provider package is not installed."),
        InvalidDataException when scope == ApiProblemScope.Integrations => new(400, "Invalid request", "invalid_package", "The provider package could not be verified."),
        ArgumentException e when scope is ApiProblemScope.Integrations or ApiProblemScope.Rules => new(400, "Invalid request", "validation", e.Message),
        InvalidOperationException e when scope == ApiProblemScope.Integrations => new(409, "Update conflict", "operation_conflict", e.Message),
        _ => null
    };
}
