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
