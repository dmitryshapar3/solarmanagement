using DeyeSolar.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace DeyeSolar.Web.Pages;

[AllowAnonymous, EnableRateLimiting("identity-auth")]
public sealed class VerifyModel(OneTimeVerificationService verification, AccountIdentityService accounts,
    SignInManager<IdentityUser> signIn, AuthProviderOptions providers) : PageModel
{
    [BindProperty] public string Channel { get; set; } = "email";
    [BindProperty] public string Destination { get; set; } = "";
    [BindProperty] public string VerificationId { get; set; } = "";
    [BindProperty] public string Code { get; set; } = "";
    public AuthProviderOptions Providers => providers;
    public string? ErrorMessage { get; private set; }
    public string? Notice { get; private set; }
    public void OnGet() { }
    public async Task<IActionResult> OnPostStartAsync(CancellationToken ct)
    {
        try
        {
            var challenge = await verification.StartAsync(new(Channel, Destination, "login"), null, ct);
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
        try
        {
            var identity = await verification.VerifyAsync(VerificationId, Code, "login", null, ct);
            var user = identity is null ? null : await accounts.FindVerifiedAsync(identity, ct);
            if (user is null || await signIn.UserManager.IsLockedOutAsync(user)) { ErrorMessage = "The verification code is invalid or this account is unavailable."; return Page(); }
            await signIn.SignInAsync(user, isPersistent: true);
            return Redirect("/");
        }
        catch (Exception) when (!ct.IsCancellationRequested) { ErrorMessage = "Sign-in could not be completed. Please try again."; return Page(); }
    }
}
