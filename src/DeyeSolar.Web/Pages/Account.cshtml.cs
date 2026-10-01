using System.Security.Claims;
using DeyeSolar.Web.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace DeyeSolar.Web.Pages;

[Authorize, EnableRateLimiting("identity-auth")]
public sealed class AccountModel(UserManager<IdentityUser> users, OneTimeVerificationService verification,
    AccountIdentityService accounts, AuthProviderOptions providers, GoogleMobileTicketStore tickets) : PageModel
{
    [BindProperty] public string Channel { get; set; } = "email";
    [BindProperty] public string Destination { get; set; } = "";
    [BindProperty] public string VerificationId { get; set; } = "";
    [BindProperty] public string Code { get; set; } = "";
    public AuthProviderOptions Providers => providers;
    public string? Email { get; private set; }
    public string? Phone { get; private set; }
    public bool GoogleLinked { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? Notice { get; private set; }
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Sign in again.");
    public async Task OnGetAsync() => await LoadAsync();
    private async Task LoadAsync()
    {
        var user = await users.FindByIdAsync(UserId);
        if (user is null) return;
        Email = user.EmailConfirmed ? user.Email : null;
        Phone = user.PhoneNumberConfirmed ? user.PhoneNumber : null;
        GoogleLinked = (await users.GetLoginsAsync(user)).Any(login => login.LoginProvider == "Google");
    }
    public async Task<IActionResult> OnPostStartAsync(CancellationToken ct)
    {
        await LoadAsync();
        try
        {
            var challenge = await verification.StartAsync(new(Channel, Destination, "link"), UserId, ct);
            VerificationId = challenge.VerificationId;
            Notice = "A verification code was sent. It expires in 10 minutes.";
        }
        catch (VerificationRateLimitException) { ErrorMessage = "Please wait before requesting another code."; }
        catch (ArgumentException) { ErrorMessage = "Enter a valid email address or phone number with country code."; }
        catch (Exception) when (!ct.IsCancellationRequested) { ErrorMessage = "Verification is currently unavailable."; }
        return Page();
    }
    public async Task<IActionResult> OnPostCompleteAsync(CancellationToken ct)
    {
        try
        {
            var identity = await verification.VerifyAsync(VerificationId, Code, "link", UserId, ct);
            if (identity is null) ErrorMessage = "The verification code is invalid or expired.";
            else { await accounts.LinkAsync(UserId, identity, ct); Notice = "The verified identity is linked to your account."; }
        }
        catch (AccountIdentityException exception) { ErrorMessage = exception.Message; }
        catch (Exception) when (!ct.IsCancellationRequested) { ErrorMessage = "The identity could not be linked."; }
        await LoadAsync();
        return Page();
    }
    public IActionResult OnPostGoogle()
    {
        if (!providers.GoogleEnabled) return Redirect("/account");
        var properties = new AuthenticationProperties { RedirectUri = "/auth/google/complete" };
        properties.Items["solar.link.user"] = UserId;
        properties.Items["solar.oauth.once"] = tickets.StartCallback();
        return Challenge(properties, GoogleIdentityEndpoints.Scheme);
    }
}
