using System.Security.Claims;
using DeyeSolar.Web.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DeyeSolar.Web.Pages;

[Authorize]
public sealed class ChangeLanguageModel(UserLanguageService preferences, UiText text) : PageModel
{
    [BindProperty] public string Language { get; set; } = "en";
    public IActionResult OnGet() => Redirect("/settings");
    public async Task<IActionResult> OnPostAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Challenge();
        if (!await preferences.SetAsync(userId, Language)) return BadRequest(text["Choose a supported language."]);
        UserLanguageService.WriteCookie(HttpContext, Language);
        return Redirect("/settings");
    }
}
