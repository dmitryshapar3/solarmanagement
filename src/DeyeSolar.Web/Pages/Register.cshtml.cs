using DeyeSolar.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Pages;

[AllowAnonymous, EnableRateLimiting("identity-auth")]
public sealed class RegisterModel(OneTimeVerificationService verification, AccountIdentityService accounts,
    SignInManager<IdentityUser> signIn, AuthProviderOptions providers) : PageModel
{
    [BindProperty] public string Channel { get; set; } = "email";
    [BindProperty] public string Destination { get; set; } = "";
    [BindProperty] public string VerificationId { get; set; } = "";
    [BindProperty] public string Code { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    public AuthProviderOptions Providers => providers;
    public string? ErrorMessage { get; private set; }
    public string? Notice { get; private set; }
    public void OnGet() { }
    public async Task<IActionResult> OnPostStartAsync(CancellationToken ct)
    {
        try
        {
            var challenge = await verification.StartAsync(new(Channel, Destination, "register"), null, ct);
            VerificationId = challenge.VerificationId;
            Notice = "A verification code was sent. It expires in 10 minutes.";
        }
        catch (VerificationRateLimitException) { ErrorMessage = "Please wait before requesting another code."; }
        catch (ArgumentException) { ErrorMessage = "Enter a valid email address or phone number with country code."; }
        catch (Exception) when (!ct.IsCancellationRequested) { ErrorMessage = "Verification delivery is currently unavailable."; }
        return Page();
    }
    public async Task<IActionResult> OnPostCompleteAsync(CancellationToken ct)
    {
        if (!providers.RegistrationEnabled) { ErrorMessage = "Registration is currently unavailable."; return Page(); }
        if (Password.Length is < 12 or > 128) { ErrorMessage = "Use a password between 12 and 128 characters."; return Page(); }
        try
        {
            var identity = await verification.VerifyAsync(VerificationId, Code, "register", null, ct);
            if (identity is null) { ErrorMessage = "The verification code is invalid or expired."; return Page(); }
            var user = await accounts.RegisterAsync(identity, Password, ct);
            await signIn.SignInAsync(user, isPersistent: true);
            return Redirect("/");
        }
        catch (AccountIdentityException exception) { ErrorMessage = exception.Message; }
        catch (DbUpdateException) { ErrorMessage = "Sign in to your existing account to link this identity."; }
        catch (Exception) when (!ct.IsCancellationRequested) { ErrorMessage = "Registration could not be completed. Request a new code and try again."; }
        return Page();
    }
}
