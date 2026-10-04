using System.Security.Claims;
using DeyeSolar.Web.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Auth;

public static class GoogleIdentityEndpoints
{
    public const string Scheme = "Google";
    public static void MapGoogleIdentity(this WebApplication app)
    {
        app.MapGet("/auth/google", (HttpContext context, AuthProviderOptions options, GoogleMobileTicketStore tickets) =>
        {
            if (!options.GoogleEnabled) return Results.Redirect("/login?error=google_unavailable");
            GoogleMobileFlow? flow = null;
            var query = context.Request.Query;
            if (query.ContainsKey("linkTicket")) flow = tickets.TakeLink(query["linkTicket"].ToString());
            else if (query["mobile"].ToString() == "true")
            {
                var challenge = query["codeChallenge"].ToString();
                var state = query["state"].ToString();
                if (GoogleMobileTicketStore.ValidFlow(challenge, state)) flow = new(challenge, state, null);
            }
            if ((query.ContainsKey("linkTicket") || query["mobile"].ToString() == "true") && flow is null)
                return Results.BadRequest(new IdentityApiError("The sign-in request is invalid or expired."));
            var properties = new AuthenticationProperties { RedirectUri = "/auth/google/complete" };
            properties.Items["solar.oauth.once"] = tickets.StartCallback();
            if (flow is not null)
            {
                properties.Items["solar.mobile.challenge"] = flow.CodeChallenge;
                properties.Items["solar.mobile.state"] = flow.State;
                if (flow.LinkingUserId is not null) properties.Items["solar.link.user"] = flow.LinkingUserId;
            }
            return Results.Challenge(properties, [Scheme]);
        }).AllowAnonymous().RequireRateLimiting("identity-auth");

        app.MapGet("/auth/google/complete", async Task<IResult> (HttpContext context, SignInManager<IdentityUser> signIn,
            AccountIdentityService accounts, GoogleMobileTicketStore tickets, CancellationToken ct) =>
        {
            var external = await context.AuthenticateAsync(IdentityConstants.ExternalScheme);
            if (!external.Succeeded || external.Principal is null || external.Properties is null)
                return Results.Redirect("/login?error=google_failed");
            var properties = external.Properties.Items;
            properties.TryGetValue("solar.link.user", out var linkingUserId);
            if (!properties.TryGetValue("solar.oauth.once", out var callbackKey) || callbackKey is null || !tickets.TakeCallback(callbackKey))
            {
                await context.SignOutAsync(IdentityConstants.ExternalScheme);
                return Results.Redirect("/login?error=google_failed");
            }
            GoogleMobileFlow? flow = null;
            if (properties.TryGetValue("solar.mobile.challenge", out var challenge)
                && properties.TryGetValue("solar.mobile.state", out var state)
                && challenge is not null && state is not null && GoogleMobileTicketStore.ValidFlow(challenge, state))
                flow = new(challenge, state, linkingUserId);
            try
            {
                var subject = external.Principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
                var email = external.Principal.FindFirstValue(ClaimTypes.Email) ?? "";
                var verified = external.Principal.FindFirstValue("google:email_verified") == "true";
                if (flow?.LinkingUserId is not null)
                {
                    // OAuth proves the Google identity, but a callback cannot authorize an account mutation.
                    // The initiating app must still prove its PKCE secret AND its authenticated account at exchange.
                    var normalized = AccountIdentityService.ValidateGoogle(subject, email, verified);
                    var pending = tickets.CreatePendingLink(subject, normalized, flow);
                    return Results.Redirect($"deyesolar://auth/callback?code={Uri.EscapeDataString(pending)}&state={Uri.EscapeDataString(flow.State)}");
                }
                if (linkingUserId is not null)
                {
                    var owner = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
                    if (!owner.Succeeded || owner.Principal?.FindFirstValue(ClaimTypes.NameIdentifier) != linkingUserId)
                        throw new AccountIdentityException("link_failed", "Sign in to the account that started linking Google.");
                }
                var user = await accounts.GoogleAsync(subject, email, verified, linkingUserId, ct);
                if (await signIn.UserManager.IsLockedOutAsync(user))
                    throw new AccountIdentityException("account_unavailable", "This account is currently unavailable.");
                if (flow is not null)
                {
                    var code = tickets.Create(user.Id, flow);
                    return Results.Redirect($"deyesolar://auth/callback?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(flow.State)}");
                }
                await signIn.SignInAsync(user, isPersistent: true);
                return Results.Redirect(linkingUserId is null ? "/" : "/account?linked=google");
            }
            catch (AccountIdentityException exception)
            {
                app.Logger.LogWarning("Google identity completion denied ({Reason}).", exception.Code);
                return Failure(flow, exception.Code);
            }
            catch (DbUpdateException)
            {
                app.Logger.LogWarning("Google identity completion denied ({Reason}).", "link_required");
                return Failure(flow, "link_required");
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                app.Logger.LogWarning("Google identity completion denied ({Reason}).", "google_failed");
                return Failure(flow, "google_failed");
            }
            finally { await context.SignOutAsync(IdentityConstants.ExternalScheme); }
        }).AllowAnonymous().RequireRateLimiting("identity-auth");
    }

    private static IResult Failure(GoogleMobileFlow? flow, string code) => flow is null
        ? Results.Redirect($"/login?error={Uri.EscapeDataString(code)}")
        : Results.Redirect($"deyesolar://auth/callback?error={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(flow.State)}");
}
