using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
namespace DeyeSolar.Web.Auth;
public sealed record AccountSecurityProof(string? CurrentPassword = null, string? VerificationId = null, string? Code = null);
public sealed record AccountPasswordChange(AccountSecurityProof Proof, string NewPassword);
public sealed class AccountSecurityException(string code, string message, int status = 409) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
// Account entry points coordinate fresh proof with focused account operations.
public sealed class AccountSecurityService(UserManager<IdentityUser> users, IAccountSessionStore sessions,
    AccountFreshProofVerifier proofs, AccountDataExporter exports, AccountDeletionService deletion)
{
    public Task<VerificationStartResponse> StartProofAsync(ClaimsPrincipal actor, string channel, string destination, CancellationToken ct)
        => proofs.StartProofAsync(actor, channel, destination, ct);
    public async Task ChangePasswordAsync(ClaimsPrincipal actor, AccountPasswordChange request, CancellationToken ct)
    {
        if (request.NewPassword is not { Length: >= 12 and <= 128 }) throw new AccountSecurityException("invalid_password", "Use a password between 12 and 128 characters.", 400);
        var user = await proofs.ProveAsync(actor, request.Proof, ct);
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var result = await users.ResetPasswordAsync(user, token, request.NewPassword);
        if (!result.Succeeded) throw new AccountSecurityException("password_change_failed", "The new password could not be saved.", 400);
        await sessions.RevokeUserAsync(user.Id, ct);
    }
    public async Task RevokeAllAsync(ClaimsPrincipal actor, AccountSecurityProof proof, CancellationToken ct)
    {
        var user = await proofs.ProveAsync(actor, proof, ct);
        var result = await users.UpdateSecurityStampAsync(user);
        if (!result.Succeeded) throw new AccountSecurityException("revoke_failed", "Account sessions could not be revoked.");
        await sessions.RevokeUserAsync(user.Id, ct);
    }
    public async Task<object> ExportAsync(ClaimsPrincipal actor, AccountSecurityProof proof, CancellationToken ct)
        => await exports.ExportAsync(await proofs.ProveAsync(actor, proof, ct), ct);
    public async Task DeleteAsync(ClaimsPrincipal actor, AccountSecurityProof proof, CancellationToken ct)
        => await deletion.DeleteAsync(await proofs.ProveAsync(actor, proof, ct), ct);
}
