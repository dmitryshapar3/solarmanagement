using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using DeyeSolar.Web.Data;

namespace DeyeSolar.Web.Api;

public class MobileAuthService
{
    private readonly UserManager<IdentityUser> _userManager;
    private readonly SignInManager<IdentityUser> _signInManager;
    private readonly MobileSessionStore _sessions;
    private readonly InstallationMembershipService _memberships;

    public MobileAuthService(
        UserManager<IdentityUser> userManager,
        SignInManager<IdentityUser> signInManager,
        MobileSessionStore sessions,
        InstallationMembershipService memberships)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _sessions = sessions;
        _memberships = memberships;
    }

    public async Task<MobileSession?> SignInAsync(MobileLoginRequest request)
    {
        var user = await FindAndCheckPasswordAsync(request);
        if (user is null) return null;
        var membership = await _memberships.GetForUserAsync(user.Id);
        return membership is null ? null : _sessions.Create(user.Id, user.UserName ?? request.Username, user.SecurityStamp, membership.InstallationId);
    }

    public async Task<IdentityUser?> FindAndCheckPasswordAsync(MobileLoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Length > 254
            || string.IsNullOrEmpty(request.Password) || request.Password.Length > 128) return null;
        var normalized = request.Username.Trim();
        var user = await _userManager.FindByNameAsync(normalized)
            ?? await _userManager.FindByEmailAsync(normalized)
            ?? await _userManager.Users.SingleOrDefaultAsync(u => u.PhoneNumber == normalized && u.PhoneNumberConfirmed);
        if (user == null) return null;
        var membership = await _memberships.GetForUserAsync(user.Id);
        if (membership == null || !user.EmailConfirmed && !user.PhoneNumberConfirmed && membership.InstallationId != InstallationIds.Legacy)
            return null;
        var result = await _signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        return result.Succeeded ? user : null;
    }

    public void SignOut(string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader) ||
            !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _sessions.Revoke(authorizationHeader["Bearer ".Length..].Trim());
    }
}
