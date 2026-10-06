using Microsoft.AspNetCore.Identity;

namespace DeyeSolar.Web.Auth;

public sealed record CodeSignInStartRequest(string Channel, string Destination);

/// <summary>One verified code signs in an existing identity or creates a new private account.</summary>
public sealed class UnifiedCodeSignIn(OneTimeVerificationService verification, AccountIdentityService accounts,
    UserManager<IdentityUser> users, AuthProviderOptions options)
{
    public Task<VerificationStartResponse> StartAsync(string channel, string destination, CancellationToken ct)
        // The challenge and delivery response never disclose whether the destination is registered.
        => verification.StartAsync(new(channel, destination, "signin"), null, ct);

    public async Task<IdentityUser> CompleteAsync(string verificationId, string code, CancellationToken ct)
    {
        var identity = await verification.VerifyAsync(verificationId, code, "signin", null, ct)
            ?? throw new AccountIdentityException("invalid_code", "The verification code is invalid or expired.");
        var user = await accounts.FindVerifiedAsync(identity, ct);
        if (user is null)
        {
            if (!options.RegistrationEnabled)
                throw new AccountIdentityException("registration_disabled", "Registration is currently unavailable.");
            // RegisterAsync atomically creates the user, owned installation and existing billing policy.
            // Identity uniqueness still rejects an unverified/foreign collision rather than merging it.
            user = await accounts.RegisterAsync(identity, null, ct);
        }
        if (await users.IsLockedOutAsync(user))
            throw new AccountIdentityException("account_unavailable", "This account is currently unavailable.");
        return user;
    }
}
