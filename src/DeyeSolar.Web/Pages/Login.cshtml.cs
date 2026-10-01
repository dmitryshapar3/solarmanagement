using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using DeyeSolar.Web.Api;
using DeyeSolar.Web.Auth;

namespace DeyeSolar.Web.Pages;

[AllowAnonymous, EnableRateLimiting("identity-auth")]
public class LoginModel : PageModel
{
    private readonly SignInManager<IdentityUser> _signInManager;
    private readonly MobileAuthService _auth;
    public AuthProviderOptions Providers { get; }

    public LoginModel(SignInManager<IdentityUser> signInManager, MobileAuthService auth, AuthProviderOptions providers)
    {
        _signInManager = signInManager;
        _auth = auth;
        Providers = providers;
    }

    [BindProperty]
    public string Username { get; set; } = "";

    [BindProperty]
    public string Password { get; set; } = "";

    public string? ErrorMessage { get; set; }

    public void OnGet()
    {
        ErrorMessage = Request.Query["error"].ToString() switch
        {
            "link_required" => "Sign in to your existing account, then open Account to link Google.",
            "link_conflict" => "This identity already belongs to another account.",
            "google_unavailable" => "Google sign-in is not configured yet.",
            "google_failed" => "Google sign-in could not be completed. Please try again.",
            "registration_disabled" => "Registration is currently unavailable.",
            _ => null
        };
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrEmpty(Username) || string.IsNullOrEmpty(Password))
        {
            ErrorMessage = "Username and password are required.";
            return Page();
        }

        var user = await _auth.FindAndCheckPasswordAsync(new(Username, Password));
        if (user is not null)
        {
            await _signInManager.SignInAsync(user, isPersistent: true);
            return Redirect("/");
        }

        ErrorMessage = "Invalid username or password.";
        return Page();
    }
}
