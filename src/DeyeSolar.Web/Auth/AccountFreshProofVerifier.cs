using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
namespace DeyeSolar.Web.Auth;
public sealed class AccountFreshProofVerifier(UserManager<IdentityUser> users, SignInManager<IdentityUser> signIn, IAccountSessionStore sessions, OneTimeVerificationService verification)
{
    private async Task<IdentityUser> ActorAsync(ClaimsPrincipal actor, CancellationToken ct)
    {
        var id = actor.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = id is null ? null : await users.FindByIdAsync(id);
        if (actor.Identity?.IsAuthenticated != true || !AccountSessionValidator.IsActiveUser(user,
            actor.FindFirstValue(InstallationAccessAuthorizer.StampClaim), DateTimeOffset.UtcNow))
            throw new AccountSecurityException("session_invalid", "Sign in again.", 401);
        var token = actor.FindFirstValue(InstallationAccessAuthorizer.SessionClaim);
        var session = token is null ? null : await sessions.FindAsync(token, ct);
        if (!AccountSessionValidator.MatchesAccount(session, user!.Id, user.SecurityStamp))
            throw new AccountSecurityException("session_invalid", "Sign in again.", 401);
        return user!;
    }
    public async Task<IdentityUser> ProveAsync(ClaimsPrincipal actor, AccountSecurityProof? proof, CancellationToken ct)
    {
        var user = await ActorAsync(actor, ct);
        if (proof?.CurrentPassword is { Length: > 0 and <= 128 } password)
        {
            var result = await signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
            if (result.Succeeded) return user;
        }
        else if (proof?.VerificationId is { } id && proof.Code is { } code)
        {
            var identity = await verification.VerifyAsync(id, code, "security", user.Id, ct);
            if (identity is not null && IsLinked(user, identity)) return user;
        }
        throw new AccountSecurityException("fresh_proof_required", "Confirm your current password or a new verification code before continuing.", 401);
    }
    private bool IsLinked(IdentityUser user, VerifiedIdentity identity) => identity.Channel == "phone"
        ? user.PhoneNumberConfirmed && user.PhoneNumber == identity.Destination
        : user.EmailConfirmed && users.NormalizeEmail(user.Email) == users.NormalizeEmail(identity.Destination);
    public async Task<VerificationStartResponse> StartProofAsync(ClaimsPrincipal actor, string channel, string destination, CancellationToken ct)
    {
        var user = await ActorAsync(actor, ct);
        if (!OneTimeVerificationService.TryNormalize(channel, destination, out var normalized) || !IsLinked(user, new(channel, normalized)))
            throw new AccountSecurityException("identity_not_linked", "Use an identity already verified for this account.", 400);
        return await verification.StartAsync(new(channel, normalized, "security"), user.Id, ct);
    }
}
