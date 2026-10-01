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

    private readonly MobileSessionStore _sessions;
    private readonly UserManager<IdentityUser> _users;
    private readonly InstallationMembershipService _memberships;

    public MobileBearerAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        MobileSessionStore sessions,
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
        var session = _sessions.Find(token);
        if (session == null)
            return AuthenticateResult.Fail("Invalid or expired mobile token.");

        var user = await _users.FindByIdAsync(session.UserId);
        var membership = await _memberships.GetForUserAsync(session.UserId, Context.RequestAborted);
        if (user == null || membership == null || await _users.IsLockedOutAsync(user)
            || session.SecurityStamp is not null && session.SecurityStamp != user.SecurityStamp
            || session.InstallationId is not null && session.InstallationId != membership.InstallationId)
        {
            _sessions.Revoke(token);
            return AuthenticateResult.Fail("Invalid or expired mobile token.");
        }
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, session.UserId),
            new Claim(ClaimTypes.Name, session.UserName),
            new Claim(InstallationIds.ClaimType, membership.InstallationId),
            new Claim(InstallationIds.RoleClaimType, membership.Role)
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return AuthenticateResult.Success(ticket);
    }
}
