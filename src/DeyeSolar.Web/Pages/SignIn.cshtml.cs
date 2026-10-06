using DeyeSolar.Web.Api;
using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
namespace DeyeSolar.Web.Pages;
[AllowAnonymous, EnableRateLimiting("identity-auth")]
public sealed class SignInModel(UnifiedCodeSignIn codes, MobileAuthService passwords, SignInManager<IdentityUser> signIn, AuthProviderOptions providers) : PageModel
{
    [BindProperty(SupportsGet=true)] public string Mode { get; set; } = "code";
    [BindProperty(SupportsGet=true)] public string? ReturnUrl { get; set; }
    [BindProperty] public string Destination { get; set; } = "";
    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public string VerificationId { get; set; } = "";
    [BindProperty] public string Code { get; set; } = "";
    [BindProperty] public string Language { get; set; } = "en";
    public AuthProviderOptions Providers => providers;
    public string? ErrorMessage { get; private set; }
    [BindProperty] public DateTimeOffset? ExpiresAt { get; set; }
    [BindProperty] public int RetryAfterSeconds { get; set; }
    [TempData] public string? PendingVerificationId { get; set; }
    [TempData] public string? PendingDestination { get; set; }
    [TempData] public string? PendingExpiresAt { get; set; }
    public bool CodeAvailable => providers.EmailEnabled || providers.PhoneEnabled;
    private string SafeReturn => Url.IsLocalUrl(ReturnUrl) && ReturnUrl is not null && !ReturnUrl.StartsWith("/signin", StringComparison.OrdinalIgnoreCase) ? ReturnUrl : "/";
    public void OnGet()
    {
        if (!CodeAvailable) Mode = "password";
        else if (Mode != "password") Mode = "code";
        if (Mode == "code" && PendingVerificationId is {} pending)
        {
            VerificationId = pending; Destination = PendingDestination ?? "";
            ExpiresAt = DateTimeOffset.TryParse(PendingExpiresAt, out var expires) ? expires : null;
            PendingVerificationId = PendingDestination = PendingExpiresAt = null;
        }
        ErrorMessage = Request.Query["error"].ToString() switch
        {
            "link_required" => "Sign in to your existing account, then open Account to link Google.",
            "link_conflict" => "This identity already belongs to another account.",
            "google_failed" or "apple_failed" => "Sign-in could not be completed. Please try again.",
            "registration_disabled" => "Registration is currently unavailable.", _ => null
        };
    }
    public async Task<IActionResult> OnPostStartAsync(CancellationToken ct)
    {
        Mode = "code";
        try
        {
            var challenge = await codes.StartAsync(Destination.Trim().Contains('@') ? "email" : "phone", Destination.Trim(), ct);
            VerificationId=challenge.VerificationId; ExpiresAt=challenge.ExpiresAt; RetryAfterSeconds=challenge.RetryAfterSeconds;
            ModelState.Remove(nameof(VerificationId));
        }
        catch (VerificationRateLimitException) { ErrorMessage="Please wait before requesting another code."; }
        catch (ArgumentException) { ErrorMessage="Enter a valid email address or phone number with country code."; }
        catch (Exception) when (!ct.IsCancellationRequested) { ErrorMessage="Verification delivery is currently unavailable."; }
        return Page();
    }
    public async Task<IActionResult> OnPostCompleteAsync(CancellationToken ct)
    {
        Mode="code";
        try { var user=await codes.CompleteAsync(VerificationId,Code,ct); await signIn.SignInAsync(user,isPersistent:true); return LocalRedirect(SafeReturn); }
        catch (AccountIdentityException error) { ErrorMessage=error.Message; }
        catch (Exception) when (!ct.IsCancellationRequested) { ErrorMessage="Sign-in could not be completed. Please try again."; }
        Code=""; ModelState.Remove(nameof(Code)); return Page();
    }
    public async Task<IActionResult> OnPostPasswordAsync()
    {
        Mode="password";
        var user=await passwords.FindAndCheckPasswordAsync(new(Username,Password));
        if(user is not null) { await signIn.SignInAsync(user,isPersistent:true); return LocalRedirect(SafeReturn); }
        ErrorMessage="Invalid username or password."; Password="";ModelState.Remove(nameof(Password));return Page();
    }
    public IActionResult OnPostLanguage()
    {
        var language=UiText.Normalize(Language);if(language is null)return BadRequest();
        UserLanguageService.WriteCookie(HttpContext,language);
        if (Mode != "password" && VerificationId.Length > 0)
        {
            PendingVerificationId = VerificationId; PendingDestination = Destination;
            PendingExpiresAt = ExpiresAt?.ToString("O");
        }
        return Redirect("/signin?mode="+(Mode=="password"?"password":"code")+"&returnUrl="+Uri.EscapeDataString(SafeReturn));
    }
}
