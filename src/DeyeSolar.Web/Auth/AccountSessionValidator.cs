using System.Security.Claims;
using DeyeSolar.Web.Api;
using Microsoft.AspNetCore.Identity;

namespace DeyeSolar.Web.Auth;

/// <summary>Pure checks over existing reads; account authentication never depends on tenant membership.</summary>
public static class AccountSessionValidator
{
    public static bool IsActiveUser([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] IdentityUser? user, string? stamp, DateTimeOffset now)
        => user is not null && stamp is not null && stamp == user.SecurityStamp
            && (!user.LockoutEnabled || user.LockoutEnd is null || user.LockoutEnd <= now);

    public static bool MatchesAccount(MobileSession? session, string? userId, string? stamp)
        => session is not null && userId is not null && stamp is not null
            && session.UserId == userId && session.SecurityStamp == stamp;

    public static bool MatchesAccount(MobileSession? session, ClaimsPrincipal actor)
        => actor.Identity?.IsAuthenticated == true && MatchesAccount(session,
            actor.FindFirstValue(ClaimTypes.NameIdentifier), actor.FindFirstValue(InstallationAccessAuthorizer.StampClaim));

    public static bool MatchesInstallation(MobileSession? session, string? userId, string? stamp, string installationId)
        => MatchesAccount(session, userId, stamp) && session!.InstallationId == installationId;
}
