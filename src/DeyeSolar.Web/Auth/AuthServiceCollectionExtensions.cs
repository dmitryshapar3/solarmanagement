using Microsoft.Extensions.DependencyInjection.Extensions;
using DeyeSolar.Web.Api;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Threading.RateLimiting;
using DeyeSolar.Web.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;

namespace DeyeSolar.Web.Auth;

public static class AuthServiceCollectionExtensions
{
    // Both the authorization request and code redemption must use the same configured callback origin.
    // Reverse-proxy and arbitrary Host/Forwarded headers cannot select an OAuth callback destination.
    public static IApplicationBuilder UseAccountIdentityOrigin(this IApplicationBuilder app)
    {
        var origin = new Uri(app.ApplicationServices.GetRequiredService<AuthProviderOptions>().PublicBaseUrl);
        return app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/auth/google") || context.Request.Path == "/signin-google"
                || context.Request.Path == "/account")
            {
                context.Request.Scheme = origin.Scheme;
                context.Request.Host = origin.IsDefaultPort ? new HostString(origin.IdnHost) : new HostString(origin.IdnHost, origin.Port);
                context.Request.PathBase = PathString.Empty;
            }
            await next(context);
        });
    }

    public static IServiceCollection AddAccountSecurity(this IServiceCollection services)
    {
        services.AddScoped<AccountSecurityService>();
        services.AddScoped<AccountFreshProofVerifier>();
        services.AddScoped<AccountDataExporter>();
        services.AddScoped<AccountDeletionService>();
        services.AddScoped<AccountOffboardingRecovery>();
        return services;
    }

    public static IServiceCollection AddAccountIdentities(this IServiceCollection services, AuthProviderOptions providers)
    {
        if (!Uri.TryCreate(providers.PublicBaseUrl, UriKind.Absolute, out var publicUri) || publicUri.Scheme != "https"
            || publicUri.UserInfo.Length != 0 || publicUri.Query.Length != 0 || publicUri.Fragment.Length != 0 || publicUri.AbsolutePath != "/")
            throw new InvalidOperationException("Auth:PublicBaseUrl must be an HTTPS origin.");
        services.AddSingleton(providers);
        services.AddScoped<CurrentInstallation>();
        services.AddScoped<InstallationMembershipService>();
        services.AddScoped<IUserClaimsPrincipalFactory<IdentityUser>, InstallationClaimsPrincipalFactory>();
        services.AddScoped<AccountIdentityService>();
        services.TryAddSingleton<IAccountSessionStore>(provider => provider.GetRequiredService<MobileSessionStore>());
        services.AddScoped<IInstallationAccessAuthorizer, InstallationAccessAuthorizer>();
        services.AddScoped<InteractiveSecurityContext>();
        services.AddHttpContextAccessor();
        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);
        services.ConfigureApplicationCookie(options =>
        {
            var signingIn = options.Events.OnSigningIn;
            options.Events.OnSigningIn = async context =>
            {
                await signingIn(context);
                var principal = context.Principal!;
                var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
                var installationId = principal.FindFirstValue(InstallationIds.ClaimType);
                if (userId is null) return;
                var store = context.HttpContext.RequestServices.GetRequiredService<IAccountSessionStore>();
                var session = await store.CreateAsync(userId, principal.Identity?.Name ?? "", principal.FindFirstValue(InstallationAccessAuthorizer.StampClaim), installationId, context.HttpContext.RequestAborted);
                var identity = (ClaimsIdentity)principal.Identity!;
                if (installationId is null) identity.AddClaim(new(InstallationIds.ClaimType, ""));
                identity.AddClaim(new(InstallationAccessAuthorizer.SessionClaim, session.Token));
            };
            var signingOut = options.Events.OnSigningOut;
            options.Events.OnSigningOut = async context =>
            {
                if (context.HttpContext.User.FindFirstValue(InstallationAccessAuthorizer.SessionClaim) is { } token)
                    await context.HttpContext.RequestServices.GetRequiredService<IAccountSessionStore>().RevokeAsync(token, context.HttpContext.RequestAborted);
                await signingOut(context);
            };
            var validating = options.Events.OnValidatePrincipal;
            options.Events.OnValidatePrincipal = async context =>
            {
                var token = context.Principal?.FindFirstValue(InstallationAccessAuthorizer.SessionClaim);
                await validating(context);
                if (context.Principal?.Identity?.IsAuthenticated != true) return;
                var store = context.HttpContext.RequestServices.GetRequiredService<IAccountSessionStore>();
                if (token is null)
                {
                    if (store is MobileSessionStore { IsPersistent: true }) context.RejectPrincipal();
                    return;
                }
                var session = await store.FindAsync(token, context.HttpContext.RequestAborted);
                if (!AccountSessionValidator.MatchesAccount(session, context.Principal)) { context.RejectPrincipal(); return; }
                var identity = (ClaimsIdentity)context.Principal.Identity;
                foreach (var claim in identity.FindAll(InstallationIds.ClaimType).Concat(identity.FindAll(InstallationIds.RoleClaimType)).ToArray()) identity.RemoveClaim(claim);
                // Identity regeneration may choose a different remaining membership. Account
                // authentication survives membership removal; tenant selection never follows it.
                identity.AddClaim(new(InstallationIds.ClaimType, session!.InstallationId ?? ""));
                var memberships = context.HttpContext.RequestServices.GetRequiredService<InstallationMembershipService>();
                if (await memberships.ResolveAsync(context.Principal, context.HttpContext.RequestAborted) is { } member)
                    identity.AddClaim(new(InstallationIds.RoleClaimType, member.Role));
                if (identity.FindFirst(InstallationAccessAuthorizer.SessionClaim) is null)
                    identity.AddClaim(new(InstallationAccessAuthorizer.SessionClaim, token));
            };
        });
        services.AddSingleton<OneTimeVerificationService>();
        services.AddSingleton<GoogleMobileTicketStore>();
        services.AddSingleton<IIdentityVerificationDelivery, IdentityVerificationDelivery>();
        services.AddHttpClient(IdentityVerificationDelivery.ClientName, client => client.Timeout = TimeSpan.FromSeconds(15))
            .RemoveAllLoggers().ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        if (providers.GoogleEnabled)
        {
            services.AddAuthentication().AddGoogle(GoogleIdentityEndpoints.Scheme, options =>
            {
                options.ClientId = providers.GoogleClientId;
                options.ClientSecret = providers.GoogleClientSecret;
                options.SignInScheme = IdentityConstants.ExternalScheme;
                options.CallbackPath = "/signin-google";
                options.UsePkce = true;
                options.SaveTokens = false;
                options.Events.OnCreatingTicket = context =>
                {
                    var verified = context.User.TryGetProperty("verified_email", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.True
                        || context.User.TryGetProperty("email_verified", out value) && value.ValueKind == System.Text.Json.JsonValueKind.True;
                    context.Identity?.AddClaim(new Claim("google:email_verified", verified ? "true" : "false"));
                    return Task.CompletedTask;
                };
                options.Events.OnRemoteFailure = context =>
                {
                    context.HandleResponse();
                    var properties = context.Properties?.Items;
                    if (properties is not null
                        && properties.TryGetValue("solar.mobile.challenge", out var challenge)
                        && properties.TryGetValue("solar.mobile.state", out var state)
                        && challenge is not null && state is not null && GoogleMobileTicketStore.ValidFlow(challenge, state))
                        context.Response.Redirect($"deyesolar://auth/callback?error=google_failed&state={Uri.EscapeDataString(state)}");
                    else context.Response.Redirect("/login?error=google_failed");
                    return Task.CompletedTask;
                };
            });
        }
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await context.HttpContext.Response.WriteAsJsonAsync(new IdentityApiError("Too many authentication requests. Please wait and try again."), cancellationToken);
            };
            options.AddPolicy("identity-auth", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
        });
        return services;
    }
}
