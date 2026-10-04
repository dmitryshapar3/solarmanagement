using DeyeSolar.Web.Auth;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Identity;
using DeyeSolar.Web.Data;

namespace DeyeSolar.Web.Api;

public class MobileBearerAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "MobileBearer";

    private readonly IAccountSessionStore _sessions;
    private readonly UserManager<IdentityUser> _users;
    private readonly InstallationMembershipService _memberships;

    public MobileBearerAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IAccountSessionStore sessions,
        UserManager<IdentityUser> users,
        InstallationMembershipService memberships)
        : base(options, logger, encoder)
    {
        _sessions = sessions;
        _users = users;
        _memberships = memberships;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authorization) ||
            !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = authorization["Bearer ".Length..].Trim();
        var session = await _sessions.FindAsync(token, Context.RequestAborted);
        if (session == null)
            return AuthenticateResult.Fail("Invalid or expired mobile token.");

        var user = await _users.FindByIdAsync(session.UserId);
        if (user == null || await _users.IsLockedOutAsync(user) || session.SecurityStamp != user.SecurityStamp)
        {
            await _sessions.RevokeAsync(token, Context.RequestAborted);
            return AuthenticateResult.Fail("Invalid or expired mobile token.");
        }
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, session.UserId),
            new Claim(InstallationAccessAuthorizer.StampClaim, user.SecurityStamp ?? ""),
            new Claim(InstallationAccessAuthorizer.SessionClaim, token),
            new Claim(ClaimTypes.Name, session.UserName),
            new Claim(InstallationIds.ClaimType, session.InstallationId ?? "")
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        if (await _memberships.ResolveAsync(principal, Context.RequestAborted) is { } member)
            identity.AddClaim(new(InstallationIds.RoleClaimType, member.Role));
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return AuthenticateResult.Success(ticket);
    }
}
