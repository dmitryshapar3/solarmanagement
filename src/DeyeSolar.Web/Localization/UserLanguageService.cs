using System.Security.Claims;
using Microsoft.AspNetCore.Identity;

namespace DeyeSolar.Web.Localization;

/// <summary>Preferences belong to a user, rather than their installation's shared settings.</summary>
public sealed class UserLanguageService(UserManager<IdentityUser> users)
{
    public const string ClaimType = "solar.language";
    public const string CookieName = "solar.language";
    public async Task<string?> GetAsync(string userId)
    {
        var user = await users.FindByIdAsync(userId);
        return user is null ? null : UiText.Normalize((await users.GetClaimsAsync(user))
            .LastOrDefault(claim => claim.Type == ClaimType)?.Value);
    }
    public async Task<bool> SetAsync(string userId, string language)
    {
        if (string.IsNullOrWhiteSpace(language) || UiText.Normalize(language) != language) return false;
        var user = await users.FindByIdAsync(userId);
        if (user is null) return false;
        var existing = (await users.GetClaimsAsync(user)).Where(claim => claim.Type == ClaimType).ToArray();
        IdentityResult result;
        if (existing.Length == 1) result = await users.ReplaceClaimAsync(user, existing[0], new(ClaimType, language));
        else
        {
            if (existing.Length > 0 && !(await users.RemoveClaimsAsync(user, existing)).Succeeded) return false;
            result = await users.AddClaimAsync(user, new(ClaimType, language));
        }
        return result.Succeeded;
    }
    public static void WriteCookie(HttpContext context, string language)
        => context.Response.Cookies.Append(CookieName, language, new CookieOptions
        { HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Lax,
            IsEssential = true, MaxAge = TimeSpan.FromDays(365), Path = "/" });
}
