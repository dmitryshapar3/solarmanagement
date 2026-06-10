using Microsoft.AspNetCore.Identity;

namespace DeyeSolar.Web.Api;

public class MobileAuthService
{
    private readonly UserManager<IdentityUser> _userManager;
    private readonly SignInManager<IdentityUser> _signInManager;
    private readonly MobileSessionStore _sessions;

    public MobileAuthService(
        UserManager<IdentityUser> userManager,
        SignInManager<IdentityUser> signInManager,
        MobileSessionStore sessions)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _sessions = sessions;
    }

    public async Task<MobileSession?> SignInAsync(MobileLoginRequest request)
    {
        var user = await _userManager.FindByNameAsync(request.Username)
            ?? await _userManager.FindByEmailAsync(request.Username);

        if (user == null)
            return null;

        var result = await _signInManager.CheckPasswordSignInAsync(
            user,
            request.Password,
            lockoutOnFailure: false);

        if (!result.Succeeded)
            return null;

        return _sessions.Create(user.Id, user.UserName ?? request.Username);
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
