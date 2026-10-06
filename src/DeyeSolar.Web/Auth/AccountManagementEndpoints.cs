using System.Security.Claims;
using DeyeSolar.Web.Api;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Operations;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;

namespace DeyeSolar.Web.Auth;

public sealed record ProfileUpdateRequest(string? DisplayName);
public sealed record PreferencesUpdateRequest(string? DisplayTimeZoneId);
public sealed record AccountProofRequest(AccountSecurityProof Proof);
public sealed record ExternalProofStartRequest(string Provider, string Operation = "account", string? CodeChallenge = null, string? State = null);
public sealed record ExternalProofCompleteRequest(string FlowId, string? IdentityToken = null, string? RawNonce = null, string? AuthorizationCode = null);
public static class AccountManagementEndpoints
{
    public static void MapAccountManagementApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/account").RequireAuthorization(ApiAuthorization.AuthenticatedUser).RequireRateLimiting("identity-auth");
        api.AddEndpointFilter(async (context, next) => { context.HttpContext.Response.Headers.CacheControl = "no-store"; return await next(context); });
        api.MapGet("/profile", (HttpContext context, AccountManagementService service, CancellationToken ct) => ReadAsync(() => service.ProfileAsync(UserId(context), ct)));
        api.MapPatch("/profile", (ProfileUpdateRequest request, HttpContext context, IAntiforgery csrf, AccountManagementService service, CancellationToken ct)
            => WriteAsync(context, csrf, () => service.UpdateProfileAsync(context.User, request.DisplayName, ct)));
        api.MapGet("/preferences", (HttpContext context, AccountManagementService service, CancellationToken ct) => ReadAsync(() => service.PreferencesAsync(UserId(context), ct)));
        api.MapPut("/preferences", (PreferencesUpdateRequest request, HttpContext context, IAntiforgery csrf, AccountManagementService service, CancellationToken ct)
            => WriteAsync(context, csrf, () => service.UpdatePreferencesAsync(context.User, request.DisplayTimeZoneId, ct)));
        api.MapGet("/sessions", (HttpContext context, AccountManagementService service, CancellationToken ct) => ReadAsync(() => service.SessionsAsync(context.User, ct)));
        api.MapPost("/sessions/{id:guid}/revoke", (Guid id, AccountProofRequest request, HttpContext context, IAntiforgery csrf, AccountManagementService service, CancellationToken ct)
            => WriteAsync(context, csrf, async () => { await service.RevokeSessionAsync(context.User, id, request.Proof, ct); return new { revoked = true }; }));
        api.MapPost("/sessions/revoke-others", (AccountProofRequest request, HttpContext context, IAntiforgery csrf, AccountManagementService service, CancellationToken ct)
            => WriteAsync(context, csrf, async () => { await service.RevokeOtherSessionsAsync(context.User, request.Proof, ct); return new { revoked = true }; }));
        api.MapPost("/identities/{provider}/unlink", (string provider, AccountProofRequest request, HttpContext context, IAntiforgery csrf, AccountManagementService service, CancellationToken ct)
            => WriteAsync(context, csrf, () => service.UnlinkAsync(context.User, provider, request.Proof, ct)));
        api.MapPost("/contacts/change/start", (ContactChangeStartRequest request, HttpContext context, IAntiforgery csrf, AccountManagementService service, CancellationToken ct)
            => WriteAsync(context, csrf, () => service.StartContactChangeAsync(context.User, request.Channel, request.Destination, request.Proof, ct)));
        api.MapPost("/contacts/change/complete", (ContactChangeCompleteRequest request, HttpContext context, IAntiforgery csrf, AccountManagementService service, CancellationToken ct)
            => WriteAsync(context, csrf, () => service.CompleteContactChangeAsync(context.User, request.ChallengeId, request.Code, ct)));
        api.MapPost("/export", async Task<IResult> (AccountProofRequest request, HttpContext context, IAntiforgery csrf, AccountManagementService service, CancellationToken ct) =>
        {
            await AuthenticatedMutationPolicy.EnsureAsync(context, csrf);
            try { return Results.File(await service.ExportZipAsync(context.User, request.Proof, ct), "application/zip", "smartsolar-account-export.zip"); }
            catch (AccountSecurityException error) { return ApiProblems.Describe(error); }
        });
        api.MapPost("/proof/external/start", async Task<IResult> (ExternalProofStartRequest request, HttpContext context,
            IAntiforgery csrf, AccountFreshProofVerifier fresh, ExternalAccountProofStore proofs, AuthProviderOptions providers, UserManager<IdentityUser> users, CancellationToken ct) =>
        {
            await AuthenticatedMutationPolicy.EnsureAsync(context, csrf);
            try
            {
                var user = await fresh.ActorAsync(context.User, ct);
                var provider = request.Provider.Equals("google", StringComparison.OrdinalIgnoreCase) ? "Google" : request.Provider.Equals("apple", StringComparison.OrdinalIgnoreCase) ? "Apple" : "";
                if (provider == "" || !(await users.GetLoginsAsync(user)).Any(x => x.LoginProvider == provider)) throw new AccountSecurityException("identity_not_linked", "Confirm an identity already linked to this account.", 400);
                if (provider == "Google" && !providers.GoogleEnabled || provider == "Apple" && !providers.Apple.NativeAvailable) throw new AccountSecurityException("provider_unavailable", "This sign-in provider is unavailable.", 503);
                if ((request.CodeChallenge is not null || request.State is not null) && !GoogleMobileTicketStore.ValidFlow(request.CodeChallenge ?? "", request.State ?? "")) throw new AccountSecurityException("invalid_flow", "The sign-in request is invalid.", 400);
                var flow = proofs.Start(context.User, user, provider, request.Operation);
                var url = providers.PublicBaseUrl.TrimEnd('/') + (provider == "Google" ? "/auth/google?proofFlow=" : "/auth/apple?proofFlow=") + Uri.EscapeDataString(flow.Id);
                if (request.CodeChallenge is not null) url += "&mobile=true&codeChallenge=" + Uri.EscapeDataString(request.CodeChallenge) + "&state=" + Uri.EscapeDataString(request.State!);
                return Results.Ok(new { flowId = flow.Id, expiresAt = flow.ExpiresAt, rawNonce = provider == "Apple" ? flow.RawNonce : null, authorizationUrl = provider == "Google" || providers.Apple.WebAvailable ? url : null });
            }
            catch (Exception error) when (error is AccountSecurityException or VerificationRateLimitException) { return ApiProblems.Describe(error); }
        });
        api.MapPost("/proof/external/complete", async Task<IResult> (ExternalProofCompleteRequest request, HttpContext context,
            IAntiforgery csrf, AccountFreshProofVerifier fresh, ExternalAccountProofStore proofs, ExternalProofCompletionStore completions,
            AuthProviderOptions providers, UserManager<IdentityUser> users, IAppleIdentityVerifier verifier, IAppleIdentityTokenClient tokens,
            AppleIdentityCredentialStore credentials, DeyeSolarDbContext db, CancellationToken ct) =>
        {
            await AuthenticatedMutationPolicy.EnsureAsync(context, csrf);
            try
            {
                var user = await fresh.ActorAsync(context.User, ct);
                var completed = completions.Take(request.FlowId, context.User, user, proofs);
                if (completed is not null) return Results.Ok(new { externalProofId = completed, expiresAt = DateTimeOffset.UtcNow.AddMinutes(2) });
                var flow = proofs.Find(request.FlowId);
                if (flow is null || !proofs.Owns(flow, context.User, user)) throw new AccountSecurityException("proof_failed", "Account confirmation expired. Start again.", 401);
                if (flow.Provider != "Apple" || !providers.Apple.NativeAvailable || request.RawNonce != flow.RawNonce) throw new AccountSecurityException("proof_pending", "Finish account confirmation first.", 409);
                var supplied = await verifier.VerifyAsync(request.IdentityToken ?? "", flow.RawNonce, providers.Apple.NativeClientId, ct);
                var exchanged = await tokens.ExchangeAsync(request.AuthorizationCode ?? "", providers.Apple.NativeClientId, null, ct);
                var identity = await verifier.VerifyAsync(exchanged.IdentityToken, flow.RawNonce, providers.Apple.NativeClientId, ct);
                var owner = await users.FindByLoginAsync("Apple", identity.Subject);
                if (supplied.Subject != identity.Subject || owner?.Id != user.Id) throw new AccountSecurityException("proof_failed", "Confirm the Apple identity already linked to this account.", 401);
                await credentials.SaveAsync(db, user.Id, identity.Subject, providers.Apple.NativeClientId, exchanged.RefreshToken, ct);
                return Results.Ok(new { externalProofId = proofs.Complete(flow.Id, "Apple", user.Id), expiresAt = DateTimeOffset.UtcNow.AddMinutes(2) });
            }
            catch (AccountSecurityException error) { return ApiProblems.Describe(error); }
            catch (AccountIdentityException error) { return ApiProblems.Error(error.Message, 401, error.Code); }
            catch (Exception) when (!ct.IsCancellationRequested) { return ApiProblems.Error("Account confirmation is temporarily unavailable.", 503, "proof_unavailable"); }
        });
    }
    private static string UserId(HttpContext context) => context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new AccountSecurityException("session_invalid", "Sign in again.", 401);
    private static async Task<IResult> ReadAsync<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (AccountSecurityException error) { return ApiProblems.Describe(error); }
    }
    private static async Task<IResult> WriteAsync<T>(HttpContext context, IAntiforgery csrf, Func<Task<T>> action)
    {
        await AuthenticatedMutationPolicy.EnsureAsync(context, csrf);
        try { return Results.Ok(await action()); }
        catch (Exception error) when (error is AccountSecurityException or VerificationRateLimitException) { return ApiProblems.Describe(error); }
        catch (ArgumentException) { return ApiProblems.Error("Enter a valid email address or international phone number.", 400, "invalid_contact"); }
        catch (VerificationDeliveryException) { return ApiProblems.Error("Verification delivery is temporarily unavailable.", 503, "delivery_unavailable"); }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException) { return ApiProblems.Error("Account details changed or the contact is unavailable. Start again.", 409, "account_conflict"); }
    }
}
