using System.Security.Claims;
using DeyeSolar.Web.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DeyeSolar.Web.Billing;

public sealed record AppleVerifyRequest(string SignedTransaction);
public sealed record AppleNotificationRequest(string SignedPayload);

public static class AppleBillingEndpoints
{
    public static IServiceCollection AddAppleBilling(this IServiceCollection services, AppleBillingOptions options)
    {
        options.Validate();
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IAppleSignedDataVerifier, AppleSignedDataVerifier>();
        services.AddHttpClient<IAppleAppStoreClient, AppleAppStoreClient>(http => http.Timeout = TimeSpan.FromSeconds(30))
            .RemoveAllLoggers().ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddScoped<SqlAppleSubscriptionStore>();
        services.AddScoped<IAppleSubscriptionCatalog>(provider => provider.GetRequiredService<SqlAppleSubscriptionStore>());
        services.AddScoped<IAppleSubscriptionWriter>(provider => provider.GetRequiredService<SqlAppleSubscriptionStore>());
        services.AddScoped<AppleBillingService>();
        services.TryAddSingleton<DeyeSolar.Web.Operations.WorkerHealthReporter>();
        services.TryAddSingleton<DeyeSolar.Web.Operations.IWorkerHealthReporter>(provider => provider.GetRequiredService<DeyeSolar.Web.Operations.WorkerHealthReporter>());
        services.AddHostedService<AppleSubscriptionRefreshWorker>();
        services.AddRateLimiter(limits => limits.AddConcurrencyLimiter("apple-notifications", limiter =>
        {
            limiter.PermitLimit = 4;
            limiter.QueueLimit = 0;
        }));
        return services;
    }

    public static void MapAppleBilling(this WebApplication app)
    {
        app.MapGet("/api/billing/access", async Task<IResult> (HttpContext context, IBillingAccessReader access, CancellationToken ct) =>
        {
            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();
            context.Response.Headers.CacheControl = "no-store";
            try { return Results.Ok(await access.ReadAsync(userId, ct)); }
            catch (BillingAccessException exception)
            {
                return Results.Json(new { code = "billing_account_unavailable", message = exception.Message }, statusCode: 409);
            }
        }).RequireAuthorization(ApiAuthorization.AuthenticatedUser);

        app.MapPost("/api/billing/apple/verify", async Task<IResult> (AppleVerifyRequest request, HttpContext context,
            AppleBillingService service, ILogger<AppleBillingService> logger, CancellationToken ct) =>
        {
            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();
            context.Response.Headers.CacheControl = "no-store";
            try { return Results.Ok(await service.VerifyPurchaseAsync(userId, request.SignedTransaction, ct)); }
            catch (AppleBillingException exception) { return Failure(exception); }
            catch (BillingAccessException exception)
            {
                return Results.Json(new { code = "billing_account_unavailable", message = exception.Message }, statusCode: 409);
            }
            catch (Exception exception) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("Apple subscription verification failed ({ErrorType}).", exception.GetType().Name);
                return Unavailable();
            }
        }).RequireAuthorization(ApiAuthorization.BearerUser).RequireRateLimiting("identity-auth")
            .WithMetadata(new RequestSizeLimitAttribute(98304));

        app.MapPost("/api/billing/apple/notifications", async Task<IResult> (AppleNotificationRequest request,
            AppleBillingService service, ILogger<AppleBillingService> logger, CancellationToken ct) =>
        {
            try
            {
                await service.HandleNotificationAsync(request.SignedPayload, ct);
                return Results.Ok();
            }
            catch (AppleBillingException exception) { return Failure(exception); }
            catch (Exception exception) when (!ct.IsCancellationRequested)
            {
                // Apple retries non-2xx responses; acknowledge only after durable refresh succeeds.
                logger.LogWarning("Apple subscription notification failed ({ErrorType}).", exception.GetType().Name);
                return Unavailable();
            }
        }).AllowAnonymous().RequireRateLimiting("apple-notifications")
            .WithMetadata(new RequestSizeLimitAttribute(98304));
    }

    private static IResult Failure(AppleBillingException exception) => Results.Json(new { exception.Code, Message = exception.Message },
        statusCode: exception.Retryable ? 503 : exception.Code == "apple_account_mismatch" ? 409 : 400);
    private static IResult Unavailable() => Results.Json(new { code = "apple_unavailable", message = "Apple subscription verification is temporarily unavailable. Please try again." }, statusCode: 503);
}
