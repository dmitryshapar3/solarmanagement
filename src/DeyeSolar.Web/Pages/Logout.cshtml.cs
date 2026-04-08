using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DeyeSolar.Web.Pages;

public class LogoutModel : PageModel
{
    private readonly SignInManager<IdentityUser> _signInManager;

    public LogoutModel(SignInManager<IdentityUser> signInManager)
    {
        _signInManager = signInManager;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        await _signInManager.SignOutAsync();
        return Redirect("/login");
    }
}
