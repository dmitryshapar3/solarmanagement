using System.Security.Claims;
using DeyeSolar.Web.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

using DeyeSolar.Web.Operations;

namespace DeyeSolar.Web.Auth;

public sealed record VerificationCompleteRequest(string VerificationId, string Code);
public sealed record AccountRegisterRequest(string VerificationId, string Code, string Password);
public sealed record IdentityApiError(string Message, string? Code = null);

public static class AccountIdentityEndpoints
{
    public static void MapAccountIdentityApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/auth").RequireRateLimiting("identity-auth");
        api.MapGet("/options", (AuthProviderOptions options) => Results.Ok(new
        {
            registrationEnabled = options.RegistrationEnabled && (options.EmailEnabled || options.PhoneEnabled || options.GoogleEnabled),
            googleRegistrationEnabled = options.GoogleEnabled && options.AllowGoogleRegistration,
            emailEnabled = options.EmailEnabled,
            phoneEnabled = options.PhoneEnabled,
            googleEnabled = options.GoogleEnabled
        })).AllowAnonymous();

        api.MapPost("/verification/start", async Task<IResult> (VerificationStartRequest request, HttpContext context,
            OneTimeVerificationService verification, CancellationToken ct) =>
        {
            string? linkingUserId = null;
            if (request.Purpose == "link")
            {
                var principal = await AuthenticateBearerAsync(context);
                linkingUserId = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                if (linkingUserId is null) return Results.Unauthorized();
            }
            try { return Results.Ok(await verification.StartAsync(request, linkingUserId, ct)); }
            catch (VerificationRateLimitException) { context.Response.Headers.RetryAfter = "60"; return Error("Please wait before requesting another verification code.", 429); }
            catch (ArgumentException) { return Error("Enter a valid email address or phone number, including the country code."); }
            catch (Exception) when (!ct.IsCancellationRequested) { return Error("Verification delivery is temporarily unavailable. Please try again later.", 503); }
        }).AllowAnonymous();

        api.MapPost("/register", async Task<IResult> (AccountRegisterRequest request, AuthProviderOptions options,
            OneTimeVerificationService verification, AccountIdentityService accounts, CancellationToken ct) =>
        {
            if (!options.RegistrationEnabled) return Error("Registration is currently unavailable.", 503);
            if (request.Password is null || request.Password.Length is < 12 or > 128)
                return Error("Use a password between 12 and 128 characters.");
            try
            {
                var identity = await verification.VerifyAsync(request.VerificationId, request.Code, "register", null, ct);
                if (identity is null) return Error("The verification code is invalid or expired.");
                var user = await accounts.RegisterAsync(identity, request.Password, ct);
                var session = await accounts.SessionAsync(user.Id, ct);
                return session is null ? Results.Unauthorized() : Results.Ok(session);
            }
            catch (AccountIdentityException exception) { return Error(exception.Message, 409, exception.Code); }
            catch (DbUpdateException) { return Error("Sign in to your existing account to link this identity.", 409, "link_required"); }
            catch (Exception) when (!ct.IsCancellationRequested) { return Error("The account could not be created. Please try again.", 503); }
        }).AllowAnonymous();

        api.MapPost("/verification/login", async Task<IResult> (VerificationCompleteRequest request,
            OneTimeVerificationService verification, AccountIdentityService accounts, CancellationToken ct) =>
        {
            try
            {
                var identity = await verification.VerifyAsync(request.VerificationId, request.Code, "login", null, ct);
                var user = identity is null ? null : await accounts.FindVerifiedAsync(identity, ct);
                var session = user is null ? null : await accounts.SessionAsync(user.Id, ct);
                return session is null ? Error("The verification code is invalid or this account is unavailable.", 401) : Results.Ok(session);
            }
            catch (Exception) when (!ct.IsCancellationRequested) { return Error("Verification is temporarily unavailable.", 503); }
        }).AllowAnonymous();

        api.MapPost("/identities/link", async Task<IResult> (VerificationCompleteRequest request, HttpContext context,
            OneTimeVerificationService verification, AccountIdentityService accounts, CancellationToken ct) =>
        {
            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();
            try
            {
                var identity = await verification.VerifyAsync(request.VerificationId, request.Code, "link", userId, ct);
                if (identity is null) return Error("The verification code is invalid or expired.");
                await accounts.LinkAsync(userId, identity, ct);
                return Results.NoContent();
            }
            catch (AccountIdentityException exception) { return Error(exception.Message, 409, exception.Code); }
            catch (DbUpdateException) { return Error("This identity already belongs to another account.", 409, "link_conflict"); }
            catch (Exception) when (!ct.IsCancellationRequested) { return Error("The identity could not be linked. Please try again.", 503); }
        }).RequireAuthorization(ApiAuthorization.BearerUser);

        api.MapPost("/google/link/start", (GoogleMobileLinkStartRequest request, HttpContext context,
            AuthProviderOptions options, GoogleMobileTicketStore tickets) =>
        {
            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();
            if (!options.GoogleEnabled) return Error("Google sign-in is currently unavailable.", 503);
            if (!GoogleMobileTicketStore.ValidFlow(request.CodeChallenge, request.State)) return Error("The sign-in request is invalid.");
            var ticket = tickets.StartLink(new(request.CodeChallenge, request.State, userId));
            return Results.Ok(new
            {
                authorizationUrl = $"{options.PublicBaseUrl.TrimEnd('/')}/auth/google?linkTicket={Uri.EscapeDataString(ticket)}",
                expiresAt = DateTimeOffset.UtcNow.AddMinutes(2)
            });
        }).RequireAuthorization(ApiAuthorization.BearerUser);

        api.MapPost("/google/exchange", async Task<IResult> (GoogleMobileExchangeRequest request, HttpContext context,
            GoogleMobileTicketStore tickets, AccountIdentityService accounts, CancellationToken ct) =>
        {
            try
            {
                var principal = await AuthenticateBearerAsync(context);
                var proof = tickets.Exchange(request.Code, request.CodeVerifier, principal?.FindFirstValue(ClaimTypes.NameIdentifier));
                if (proof is null)
                {
                    app.Logger.LogWarning("Google mobile exchange denied ({Reason}).", "invalid_or_expired_proof");
                    return Error("Google sign-in expired. Please start again.", 401);
                }
                var userId = proof.UserId;
                if (proof.Link is not null)
                    userId = (await accounts.GoogleAsync(proof.Link.Subject, proof.Link.Email, true, proof.Link.UserId, ct)).Id;
                var session = userId is null ? null : await accounts.SessionAsync(userId, ct);
                if (session is null)
                {
                    app.Logger.LogWarning("Google mobile exchange denied ({Reason}).", "account_unavailable");
                    return Error("Google sign-in expired. Please start again.", 401);
                }
                return Results.Ok(session);
            }
            catch (AccountIdentityException exception)
            {
                app.Logger.LogWarning("Google mobile exchange denied ({Reason}).", exception.Code);
                return Error(exception.Message, 409, exception.Code);
            }
            catch (DbUpdateException)
            {
                app.Logger.LogWarning("Google mobile exchange denied ({Reason}).", "link_conflict");
                return Error("This Google identity already belongs to another account.", 409, "link_conflict");
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                app.Logger.LogWarning("Google mobile exchange denied ({Reason}).", "google_failed");
                return Error("Google sign-in is temporarily unavailable.", 503);
            }
        }).AllowAnonymous();
    }

    private static async Task<ClaimsPrincipal?> AuthenticateBearerAsync(HttpContext context)
    {
        if (context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var bearer = await context.AuthenticateAsync(MobileBearerAuthenticationHandler.SchemeName);
            return bearer.Succeeded ? bearer.Principal : null;
        }
        return null;
    }

    private static IResult Error(string message, int status = 400, string? code = null)
        => ApiProblems.Error(message, status, code);
}
