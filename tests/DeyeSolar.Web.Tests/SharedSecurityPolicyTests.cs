using System.Security.Claims;
using DeyeSolar.Web.Api;
using DeyeSolar.Web.Auth;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace DeyeSolar.Web.Tests;

public sealed class SharedSecurityPolicyTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public async Task OnlyBearerWithoutAmbientCookieCanMutateWithoutCsrf(bool bearer, bool cookie, bool allowed)
    {
        using var services = new ServiceCollection().AddSingleton<IAuthenticationService>(new Authentication(bearer, cookie)).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        var csrf = new Antiforgery { Reject = true };
        Assert.Equal(allowed, await AuthenticatedMutationPolicy.IsAllowedAsync(context, csrf));
        Assert.Equal(allowed ? 0 : 1, csrf.Validations);
    }

    [Fact]
    public async Task MiddlewareAndEndpointShareOnlyASuccessfulRequestValidation()
    {
        var auth = new Authentication(false, true);
        using var services = new ServiceCollection().AddSingleton<IAuthenticationService>(auth).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        var csrf = new Antiforgery();
        await AuthenticatedMutationPolicy.EnsureAsync(context, csrf);
        await AuthenticatedMutationPolicy.EnsureAsync(context, csrf);
        Assert.Equal(1, csrf.Validations);
        Assert.Equal(2, auth.Authentications);
        var nextRequest = new DefaultHttpContext { RequestServices = services };
        csrf.Reject = true;
        Assert.False(await AuthenticatedMutationPolicy.IsAllowedAsync(nextRequest, csrf));
        Assert.False(await AuthenticatedMutationPolicy.IsAllowedAsync(nextRequest, csrf));
        Assert.Equal(3, csrf.Validations);
    }

    [Fact]
    public void AccountSessionSurvivesTenantRemovalButCannotAuthorizeAnotherInstallation()
    {
        var session = new MobileSession("token", "account", "owner", DateTimeOffset.UtcNow.AddHours(1), "stamp", "old-installation");
        var actor = new ClaimsPrincipal(new ClaimsIdentity([
            new(ClaimTypes.NameIdentifier, "account"), new(InstallationAccessAuthorizer.StampClaim, "stamp")], "authenticated"));
        Assert.True(AccountSessionValidator.MatchesAccount(session, actor));
        Assert.True(AccountSessionValidator.MatchesInstallation(session, "account", "stamp", "old-installation"));
        Assert.False(AccountSessionValidator.MatchesInstallation(session, "account", "stamp", "new-installation"));
        Assert.True(AccountSessionValidator.MatchesAccount(session with { InstallationId = null }, actor));
        Assert.False(AccountSessionValidator.MatchesAccount(session with { SecurityStamp = "revoked" }, actor));
        Assert.False(AccountSessionValidator.MatchesAccount(session with { UserId = "other-account" }, actor));
        Assert.False(AccountSessionValidator.MatchesAccount(null, actor));
    }

    [Fact]
    public void ActiveAccountChecksStampAndLockoutWithoutNeedingMembership()
    {
        var now = DateTimeOffset.UtcNow;
        var user = new IdentityUser { SecurityStamp = "stamp", LockoutEnabled = true, LockoutEnd = now.AddMinutes(1) };
        Assert.False(AccountSessionValidator.IsActiveUser(user, "stamp", now));
        Assert.True(AccountSessionValidator.IsActiveUser(user, "stamp", now.AddMinutes(1)));
        Assert.False(AccountSessionValidator.IsActiveUser(user, "revoked", now.AddMinutes(1)));
        Assert.False(AccountSessionValidator.IsActiveUser(user, null, now.AddMinutes(1)));
        user.LockoutEnabled = false;
        Assert.True(AccountSessionValidator.IsActiveUser(user, "stamp", now));
    }

    [Theory]
    [InlineData("Owner", true, true, true, true, true)]
    [InlineData("IntegrationManager", true, false, false, false, true)]
    [InlineData("Operator", true, true, true, false, false)]
    [InlineData("Viewer", true, false, false, false, false)]
    [InlineData("owner", false, false, false, false, false)]
    [InlineData("UnknownRole", false, false, false, false, false)]
    public void RoleMatrixKeepsInstallationPermissionsDistinct(string role, bool read, bool rules, bool control, bool settings, bool integrations)
    {
        Assert.Equal(read, InstallationPermissionPolicy.Allows(role, InstallationPermission.Read));
        Assert.Equal(rules, InstallationPermissionPolicy.Allows(role, InstallationPermission.ManageRules));
        Assert.Equal(control, InstallationPermissionPolicy.Allows(role, InstallationPermission.ControlDevices));
        Assert.Equal(settings, InstallationPermissionPolicy.Allows(role, InstallationPermission.ManageSettings));
        Assert.Equal(integrations, InstallationPermissionPolicy.Allows(role, InstallationPermission.ManageIntegrations));
        Assert.False(InstallationPermissionPolicy.Allows(role, (InstallationPermission)999));
    }

    private sealed class Authentication(bool bearer, bool cookie) : IAuthenticationService
    {
        public int Authentications { get; private set; }
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
        {
            Authentications++;
            var allowed = scheme == MobileBearerAuthenticationHandler.SchemeName ? bearer : scheme == IdentityConstants.ApplicationScheme && cookie;
            return Task.FromResult(allowed ? AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([], scheme)), scheme!)) : AuthenticateResult.NoResult());
        }
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
    }

    private sealed class Antiforgery : IAntiforgery
    {
        public bool Reject { get; set; }
        public int Validations { get; private set; }
        public Task ValidateRequestAsync(HttpContext context)
        {
            Validations++;
            return Reject ? Task.FromException(new AntiforgeryValidationException("invalid fixture token")) : Task.CompletedTask;
        }
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext context) => throw new NotSupportedException();
        public AntiforgeryTokenSet GetTokens(HttpContext context) => throw new NotSupportedException();
        public Task<bool> IsRequestValidAsync(HttpContext context) => throw new NotSupportedException();
        public void SetCookieTokenAndHeader(HttpContext context) => throw new NotSupportedException();
    }
}
