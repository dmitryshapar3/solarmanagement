using System.Collections.Concurrent;
using System.Data;
using System.Security.Claims;
using DeyeSolar.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Auth;

public sealed record AccountProfile(string? DisplayName, string? VerifiedEmail, string? VerifiedPhone);
public sealed record AccountPreferences(string? DisplayTimeZoneId);
public sealed record AccountSessionsResponse(IReadOnlyList<AccountSessionInfo> Sessions);
public sealed record ContactChangeStartRequest(string Channel, string Destination, AccountSecurityProof Proof);
public sealed record ContactChangeCompleteRequest(string ChallengeId, string Code);
public sealed record ContactChangeChallenge(string ChallengeId, DateTimeOffset ExpiresAt, int RetryAfterSeconds);
public sealed class ContactChangeStore(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, ContactChangeFlow> _flows = new();
    public void Add(string id, ContactChangeFlow flow)
    {
        foreach (var item in _flows.Where(x => x.Value.ExpiresAt <= clock.GetUtcNow())) _flows.TryRemove(item);
        if (_flows.Count >= 1000) throw new VerificationRateLimitException();
        _flows[id] = flow;
    }
    public ContactChangeFlow? Take(string id) => id is { Length: <= 128 } && _flows.TryRemove(id, out var flow) && flow.ExpiresAt > clock.GetUtcNow() ? flow : null;
}
public sealed record ContactChangeFlow(string UserId, string SessionHash, string Stamp, string ContactVersion,
    VerifiedIdentity Identity, DateTimeOffset ExpiresAt);

public sealed class AccountManagementService(UserManager<IdentityUser> users, DeyeSolarDbContext db,
    AccountFreshProofVerifier proofs, IAccountSessionStore sessions, OneTimeVerificationService verification,
    ContactChangeStore contacts, AccountZipExporter exports, AuthProviderOptions providers, AppleIdentityCredentialStore? apple = null)
{
    public const string DisplayNameClaim = "solar:display-name";
    public const string DisplayZoneClaim = "solar:display-time-zone";
    public async Task<AccountProfile> ProfileAsync(string userId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId) ?? throw new AccountSecurityException("session_invalid", "Sign in again.", 401);
        return await ProfileAsync(user);
    }
    private async Task<AccountProfile> ProfileAsync(IdentityUser user) => new((await users.GetClaimsAsync(user)).FirstOrDefault(c => c.Type == DisplayNameClaim)?.Value,
        user.EmailConfirmed ? user.Email : null, user.PhoneNumberConfirmed ? user.PhoneNumber : null);
    public async Task<AccountProfile> UpdateProfileAsync(ClaimsPrincipal actor, string? displayName, CancellationToken ct)
    {
        var user = await proofs.ActorAsync(actor, ct);
        displayName = displayName?.Trim();
        if (displayName is { Length: > 100 } || displayName?.Any(char.IsControl) == true)
            throw new AccountSecurityException("invalid_name", "Use a display name of at most 100 characters.", 400);
        await SetClaimAsync(user, DisplayNameClaim, string.IsNullOrWhiteSpace(displayName) ? null : displayName, ct);
        return await ProfileAsync(user);
    }
    public async Task<AccountPreferences> PreferencesAsync(string userId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId) ?? throw new AccountSecurityException("session_invalid", "Sign in again.", 401);
        return new((await users.GetClaimsAsync(user)).FirstOrDefault(c => c.Type == DisplayZoneClaim)?.Value);
    }
    public async Task<AccountPreferences> UpdatePreferencesAsync(ClaimsPrincipal actor, string? displayTimeZoneId, CancellationToken ct)
    {
        var user = await proofs.ActorAsync(actor, ct);
        displayTimeZoneId = displayTimeZoneId?.Trim();
        if (!string.IsNullOrEmpty(displayTimeZoneId))
        {
            if (displayTimeZoneId.Length > 128) throw new AccountSecurityException("invalid_time_zone", "Choose a valid time zone.", 400);
            try { _ = TimeZoneInfo.FindSystemTimeZoneById(displayTimeZoneId); }
            catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
            { throw new AccountSecurityException("invalid_time_zone", "Choose a valid time zone.", 400); }
        }
        await SetClaimAsync(user, DisplayZoneClaim, string.IsNullOrEmpty(displayTimeZoneId) ? null : displayTimeZoneId, ct);
        return new(string.IsNullOrEmpty(displayTimeZoneId) ? null : displayTimeZoneId);
    }
    private async Task SetClaimAsync(IdentityUser user, string type, string? value, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var old = (await users.GetClaimsAsync(user)).Where(c => c.Type == type).ToArray();
        if (old.Length > 0 && !(await users.RemoveClaimsAsync(user, old)).Succeeded || value is not null && !(await users.AddClaimAsync(user, new(type, value))).Succeeded)
            throw new AccountSecurityException("profile_save_failed", "Account preferences could not be saved.");
        await transaction.CommitAsync(ct);
    }
    public async Task<AccountSessionsResponse> SessionsAsync(ClaimsPrincipal actor, CancellationToken ct)
    {
        var user = await proofs.ActorAsync(actor, ct);
        return new(await sessions.ListAsync(user.Id, SessionToken(actor), ct));
    }
    public async Task RevokeSessionAsync(ClaimsPrincipal actor, Guid id, AccountSecurityProof proof, CancellationToken ct)
    {
        var user = await proofs.ProveAsync(actor, proof, ct, "sessions");
        if (!await sessions.RevokeSessionAsync(user.Id, id, ct)) throw new AccountSecurityException("session_not_found", "This session is no longer active.", 404);
    }
    public async Task RevokeOtherSessionsAsync(ClaimsPrincipal actor, AccountSecurityProof proof, CancellationToken ct)
    {
        var user = await proofs.ProveAsync(actor, proof, ct, "sessions");
        await sessions.RevokeOthersAsync(user.Id, SessionToken(actor), ct);
    }
    public async Task<AccountIdentitiesResponse?> UnlinkAsync(ClaimsPrincipal actor, string provider, AccountSecurityProof proof, CancellationToken ct)
    {
        var user = await proofs.ProveAsync(actor, proof, ct, "identity-unlink");
        provider = provider.Equals("google", StringComparison.OrdinalIgnoreCase) ? "Google" : provider.Equals("apple", StringComparison.OrdinalIgnoreCase) ? "Apple" : "";
        if (provider == "") throw new AccountSecurityException("invalid_provider", "Choose Google or Apple.", 400);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (db.Database.IsSqlServer()) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT [Id] FROM [AspNetUsers] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {user.Id}", ct);
        await db.Entry(user).ReloadAsync(ct);
        var logins = await users.GetLoginsAsync(user);
        var login = logins.SingleOrDefault(x => x.LoginProvider == provider);
        if (login is null) throw new AccountSecurityException("identity_not_linked", "This sign-in method is already disconnected.", 404);
        if (!await users.HasPasswordAsync(user) && !(user.EmailConfirmed && providers.EmailEnabled) && !(user.PhoneNumberConfirmed && providers.PhoneEnabled)
            && !logins.Any(x => x != login && (x.LoginProvider == "Google" && providers.GoogleEnabled || x.LoginProvider == "Apple" && providers.Apple.NativeAvailable)))
            throw new AccountSecurityException("last_sign_in_method", "Add another verified sign-in method before disconnecting this one.");
        if (provider == "Apple" && apple is not null) await apple.QueueRevokeAsync(db, user.Id, ct);
        if (!(await users.RemoveLoginAsync(user, provider, login.ProviderKey)).Succeeded) throw new AccountSecurityException("unlink_failed", "The sign-in method could not be disconnected.");
        await transaction.CommitAsync(ct);
        return new(user.EmailConfirmed ? user.Email : null, user.PhoneNumberConfirmed ? user.PhoneNumber : null, logins.Any(x => x.LoginProvider == "Google" && x != login))
            { AppleLinked = logins.Any(x => x.LoginProvider == "Apple" && x != login), HasPassword = await users.HasPasswordAsync(user) };
    }
    public async Task<ContactChangeChallenge> StartContactChangeAsync(ClaimsPrincipal actor, string channel, string destination, AccountSecurityProof proof, CancellationToken ct)
    {
        var user = await proofs.ProveAsync(actor, proof, ct, "contact-change");
        if (!OneTimeVerificationService.TryNormalize(channel, destination, out var normalized)) throw new AccountSecurityException("invalid_contact", "Enter a valid email address or international phone number.", 400);
        await EnsureAvailableAsync(user.Id, new(channel, normalized), ct);
        var challenge = await verification.StartAsync(new(channel, normalized, "contact-change"), user.Id, ct);
        contacts.Add(challenge.VerificationId, new(user.Id, ExternalAccountProofStore.Hash(SessionToken(actor)), user.SecurityStamp ?? "", ContactVersion(user), new(channel, normalized), challenge.ExpiresAt));
        return new(challenge.VerificationId, challenge.ExpiresAt, challenge.RetryAfterSeconds);
    }
    public async Task<AccountProfile> CompleteContactChangeAsync(ClaimsPrincipal actor, string challengeId, string code, CancellationToken ct)
    {
        var user = await proofs.ActorAsync(actor, ct);
        // Invalid attempts retain the challenge's retry budget; successful proof consumes it once.
        var identity = await verification.VerifyAsync(challengeId, code, "contact-change", user.Id, ct);
        if (identity is null) throw new AccountSecurityException("verification_failed", "The verification code is invalid or expired.", 400);
        var flow = contacts.Take(challengeId);
        if (flow is null || flow.UserId != user.Id || flow.Stamp != user.SecurityStamp || flow.SessionHash != ExternalAccountProofStore.Hash(SessionToken(actor)) || flow.Identity != identity)
            throw new AccountSecurityException("contact_change_expired", "Start the contact change again.", 401);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (db.Database.IsSqlServer()) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT [Id] FROM [AspNetUsers] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {user.Id}", ct);
        await db.Entry(user).ReloadAsync(ct);
        if (ContactVersion(user) != flow.ContactVersion || user.SecurityStamp != flow.Stamp) throw new AccountSecurityException("contact_changed", "Your account details changed. Start again.");
        await EnsureAvailableAsync(user.Id, identity, ct);
        if (identity.Channel == "email") { user.Email = identity.Destination; user.EmailConfirmed = true; }
        else { user.PhoneNumber = identity.Destination; user.PhoneNumberConfirmed = true; }
        if (!(await users.UpdateAsync(user)).Succeeded) throw new AccountSecurityException("contact_save_failed", "The verified contact could not be saved.");
        await transaction.CommitAsync(ct);
        return await ProfileAsync(user);
    }
    private async Task EnsureAvailableAsync(string userId, VerifiedIdentity identity, CancellationToken ct)
    {
        var used = identity.Channel == "phone" ? await users.Users.AnyAsync(u => u.Id != userId && u.PhoneNumber == identity.Destination, ct)
            : await users.Users.AnyAsync(u => u.Id != userId && u.NormalizedEmail == users.NormalizeEmail(identity.Destination), ct);
        if (used) throw new AccountSecurityException("contact_conflict", "This contact already belongs to another account.");
    }
    private static string ContactVersion(IdentityUser user) => ExternalAccountProofStore.Hash($"{user.Email}\n{user.EmailConfirmed}\n{user.PhoneNumber}\n{user.PhoneNumberConfirmed}");
    private static string SessionToken(ClaimsPrincipal actor) => actor.FindFirstValue(InstallationAccessAuthorizer.SessionClaim) ?? throw new AccountSecurityException("session_invalid", "Sign in again.", 401);
    public async Task<byte[]> ExportZipAsync(ClaimsPrincipal actor, AccountSecurityProof proof, CancellationToken ct)
        => await exports.ExportAsync(await proofs.ProveAsync(actor, proof, ct, "export"), ct);
}
