using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DeyeSolar.Web.Api;
using DeyeSolar.Web.Operations;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;

namespace DeyeSolar.Web.Auth;

public static class AppleIdentityEndpoints
{
    public static void MapAppleIdentity(this WebApplication app)
    {
        var api = app.MapGroup("/api/auth/apple").RequireRateLimiting("identity-auth");
        api.MapPost("/exchange", async Task<IResult> (AppleIdentityExchangeRequest request, AuthProviderOptions options,
            IAppleIdentityVerifier verifier, IAppleIdentityTokenClient tokens, AccountIdentityService accounts, AppleIdentityCredentialStore credentials,
            HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!options.Apple.NativeAvailable) return ApiProblems.Error("Apple sign-in is unavailable.", 503, "apple_unavailable");
            try
            {
                var supplied = await verifier.VerifyAsync(request.IdentityToken, request.RawNonce, options.Apple.NativeClientId, ct);
                var exchanged = await tokens.ExchangeAsync(request.AuthorizationCode, options.Apple.NativeClientId, null, ct);
                var identity = await verifier.VerifyAsync(exchanged.IdentityToken, request.RawNonce, options.Apple.NativeClientId, ct);
                if (identity.Subject != supplied.Subject) throw new AccountIdentityException("apple_failed", "Apple authorization did not match this identity.");
                var user = await accounts.AppleAsync(identity, options.Apple.NativeClientId, exchanged.RefreshToken, null, credentials, ct);
                return Results.Ok(await accounts.SessionAsync(user.Id, ct));
            }
            catch (AccountIdentityException e) { return ApiProblems.Error(e.Message, 409, e.Code); }
            catch (Exception) when (!ct.IsCancellationRequested) { return ApiProblems.Error("Apple sign-in is temporarily unavailable.", 503, "apple_failed"); }
        }).AllowAnonymous();
        api.MapPost("/link/start", async Task<IResult> (AppleLinkStartRequest request, HttpContext context, IAntiforgery csrf,
            AuthProviderOptions options, AccountFreshProofVerifier proof, AppleIdentityFlowStore flows, CancellationToken ct) =>
        {
            await AuthenticatedMutationPolicy.EnsureAsync(context, csrf); context.Response.Headers.CacheControl = "no-store";
            if (!options.Apple.NativeAvailable) return ApiProblems.Error("Apple sign-in is unavailable.", 503, "apple_unavailable");
            try
            {
                var user = await proof.ProveAsync(context.User, request.Proof, ct, "identity-link");
                var flow = flows.StartLink(user.Id, ExternalAccountProofStore.Hash(context.User.FindFirstValue(InstallationAccessAuthorizer.SessionClaim)!), user.SecurityStamp ?? "");
                return Results.Ok(new { flowId = flow.Id, rawNonce = flow.RawNonce, expiresAt = flow.ExpiresAt,
                    authorizationUrl = options.Apple.WebAvailable ? options.PublicBaseUrl.TrimEnd('/') + "/auth/apple?linkFlow=" + Uri.EscapeDataString(flow.Id) : null });
            }
            catch (AccountSecurityException e) { return ApiProblems.Describe(e); }
        }).RequireAuthorization(ApiAuthorization.AuthenticatedUser);
        api.MapPost("/link/complete", async Task<IResult> (AppleLinkCompleteRequest request, HttpContext context, IAntiforgery csrf,
            AuthProviderOptions options, AccountFreshProofVerifier proof, AppleIdentityFlowStore flows, IAppleIdentityVerifier verifier,
            IAppleIdentityTokenClient tokens, AccountIdentityService accounts, AppleIdentityCredentialStore credentials, CancellationToken ct) =>
        {
            await AuthenticatedMutationPolicy.EnsureAsync(context, csrf); context.Response.Headers.CacheControl = "no-store";
            if (!options.Apple.NativeAvailable) return ApiProblems.Error("Apple sign-in is unavailable.", 503, "apple_unavailable");
            try
            {
                var user = await proof.ActorAsync(context.User, ct); var flow = flows.TakeLink(request.FlowId);
                if (flow is null || flow.UserId != user.Id || flow.Stamp != user.SecurityStamp || flow.RawNonce != request.RawNonce
                    || flow.SessionHash != ExternalAccountProofStore.Hash(context.User.FindFirstValue(InstallationAccessAuthorizer.SessionClaim)!))
                    throw new AccountSecurityException("link_expired", "Start linking Apple again.", 401);
                var supplied = await verifier.VerifyAsync(request.IdentityToken, flow.RawNonce, options.Apple.NativeClientId, ct);
                var exchanged = await tokens.ExchangeAsync(request.AuthorizationCode, options.Apple.NativeClientId, null, ct);
                var identity = await verifier.VerifyAsync(exchanged.IdentityToken, flow.RawNonce, options.Apple.NativeClientId, ct);
                if (supplied.Subject != identity.Subject) throw new AccountIdentityException("apple_failed", "Apple authorization did not match this identity.");
                await accounts.AppleAsync(identity, options.Apple.NativeClientId, exchanged.RefreshToken, user.Id, credentials, ct);
                return Results.Ok(await accounts.IdentitiesAsync(user.Id, ct));
            }
            catch (AccountSecurityException e) { return ApiProblems.Describe(e); }
            catch (AccountIdentityException e) { return ApiProblems.Error(e.Message, 409, e.Code); }
            catch (Exception) when (!ct.IsCancellationRequested) { return ApiProblems.Error("Apple sign-in is temporarily unavailable.", 503, "apple_failed"); }
        }).RequireAuthorization(ApiAuthorization.AuthenticatedUser);
        app.MapGet("/auth/apple", (HttpContext context, AuthProviderOptions options, AppleIdentityFlowStore flows, ExternalAccountProofStore proofs) =>
        {
            if (!options.Apple.WebAvailable) return Results.Redirect("/signin?error=apple_unavailable");
            var proofId = context.Request.Query["proofFlow"].ToString();
            if (proofId.Length > 0 && proofs.Find(proofId)?.Provider != "Apple") return Results.BadRequest();
            var linkId = context.Request.Query["linkFlow"].ToString(); var link = linkId.Length > 0 ? flows.FindLink(linkId) : null;
            if (linkId.Length > 0 && link is null || link is not null && proofId.Length > 0) return Results.BadRequest();
            var correlation = ExternalAccountProofStore.RandomId();
            var flow = flows.StartWeb(correlation, context.Request.Query["returnUrl"].ToString(), proofId.Length > 0 ? proofId : null, link);
            context.Response.Cookies.Append("solar.apple." + flow.Id, correlation, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.None, Path = "/auth/apple/callback", MaxAge = TimeSpan.FromMinutes(5), IsEssential = true });
            var nonce = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(flow.RawNonce))).ToLowerInvariant();
            var query = new Dictionary<string, string?> { ["client_id"] = options.Apple.ServicesId, ["redirect_uri"] = options.Apple.CallbackUrl,
                ["response_type"] = "code id_token", ["response_mode"] = "form_post", ["scope"] = "name email", ["state"] = flow.Id, ["nonce"] = nonce };
            return Results.Redirect(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString("https://appleid.apple.com/auth/authorize", query));
        }).AllowAnonymous().RequireRateLimiting("identity-auth");
        app.MapPost("/auth/apple/callback", async Task<IResult> (HttpContext context, AuthProviderOptions options,
            AppleIdentityFlowStore flows, IAppleIdentityVerifier verifier, IAppleIdentityTokenClient tokens,
            AccountIdentityService accounts, AppleIdentityCredentialStore credentials, UserManager<IdentityUser> users,
            SignInManager<IdentityUser> signIn, ExternalAccountProofStore proofs, ExternalProofCompletionStore completions, DeyeSolar.Web.Data.DeyeSolarDbContext db, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!options.Apple.WebAvailable || !context.Request.HasFormContentType || context.Request.ContentLength > 50000) return Results.Redirect("/signin?error=apple_failed");
            var form = await context.Request.ReadFormAsync(ct); var state = form["state"].ToString();
            if (state.Length > 128) return Results.BadRequest();
            var cookie = "solar.apple." + state; var flow = flows.TakeWeb(state, context.Request.Cookies[cookie] ?? "");
            context.Response.Cookies.Delete(cookie, new CookieOptions { Path = "/auth/apple/callback", Secure = true, SameSite = SameSiteMode.None });
            if (flow is null) return Results.Redirect("/signin?error=apple_failed");
            try
            {
                var supplied = await verifier.VerifyAsync(form["id_token"].ToString(), flow.RawNonce, options.Apple.ServicesId, ct);
                var exchanged = await tokens.ExchangeAsync(form["code"].ToString(), options.Apple.ServicesId, options.Apple.CallbackUrl, ct);
                var identity = await verifier.VerifyAsync(exchanged.IdentityToken, flow.RawNonce, options.Apple.ServicesId, ct);
                if (identity.Subject != supplied.Subject) throw new AccountIdentityException("apple_failed", "Apple authorization did not match this identity.");
                if (flow.LinkFlowId is { } linkId)
                {
                    var link = flows.TakeLink(linkId) ?? throw new AccountIdentityException("link_expired", "Start linking Apple again.");
                    var ticket = flows.PendingLink(link, identity, exchanged, options.Apple.ServicesId);
                    return Results.Redirect("/auth/apple/link/complete?ticket=" + Uri.EscapeDataString(ticket));
                }
                if (flow.ExternalProofFlowId is { } id)
                {
                    var owner = await users.FindByLoginAsync("Apple", identity.Subject);
                    if (owner is null || await users.IsLockedOutAsync(owner)) throw new AccountIdentityException("proof_failed", "Use the Apple identity already linked to this account.");
                    var proofId = proofs.Complete(id, "Apple", owner.Id);
                    await credentials.SaveAsync(db, owner.Id, identity.Subject, options.Apple.ServicesId, exchanged.RefreshToken, ct);
                    completions.Add(id, proofId);
                    return Results.Redirect("/settings/account?confirmed=apple");
                }
                var user = await accounts.AppleAsync(identity, options.Apple.ServicesId, exchanged.RefreshToken, null, credentials, ct);
                await signIn.SignInAsync(user, isPersistent: true); return Results.Redirect(flow.ReturnUrl);
            }
            catch (Exception) when (!ct.IsCancellationRequested) { return Results.Redirect("/signin?error=apple_failed"); }
        }).AllowAnonymous().RequireRateLimiting("identity-auth").DisableAntiforgery();
        app.MapGet("/auth/apple/link/complete", async Task<IResult> (HttpContext context, AuthProviderOptions options,
            AppleIdentityFlowStore flows, AccountFreshProofVerifier proofs, AccountIdentityService accounts, AppleIdentityCredentialStore credentials, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!options.Apple.WebAvailable) return Results.Redirect("/settings/account?error=apple_unavailable");
            try
            {
                var user = await proofs.ActorAsync(context.User, ct);
                var pending = flows.TakePending(context.Request.Query["ticket"].ToString());
                if (pending is null || pending.Flow.UserId != user.Id || pending.Flow.Stamp != user.SecurityStamp
                    || pending.Flow.SessionHash != ExternalAccountProofStore.Hash(context.User.FindFirstValue(InstallationAccessAuthorizer.SessionClaim)!))
                    throw new AccountSecurityException("link_expired", "Start linking Apple again.", 401);
                await accounts.AppleAsync(pending.Identity, pending.Audience, pending.Tokens.RefreshToken, user.Id, credentials, ct);
                return Results.Redirect("/settings/account?linked=apple");
            }
            catch (Exception) when (!ct.IsCancellationRequested) { return Results.Redirect("/settings/account?error=apple_link_failed"); }
        }).RequireAuthorization(ApiAuthorization.AuthenticatedUser).RequireRateLimiting("identity-auth");
    }
}
