using DeyeSolar.Web.Api;
using DeyeSolar.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Auth;

public sealed class AccountIdentityService(UserManager<IdentityUser> users, DeyeSolarDbContext db,
    InstallationMembershipService memberships, MobileSessionStore sessions)
{
    public async Task<MobileAuthResponse?> SessionAsync(string userId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId);
        var membership = await memberships.GetForUserAsync(userId, ct);
        if (user is null || membership is null || await users.IsLockedOutAsync(user)) return null;
        var session = sessions.Create(user.Id, user.UserName ?? "", user.SecurityStamp, membership.InstallationId);
        return new(session.Token, session.ExpiresAt, session.UserName, membership.InstallationId);
    }

    public async Task<IdentityUser?> FindVerifiedAsync(VerifiedIdentity identity, CancellationToken ct)
    {
        if (identity.Channel == "phone") return await users.Users.SingleOrDefaultAsync(u => u.PhoneNumber == identity.Destination && u.PhoneNumberConfirmed, ct);
        var email = users.NormalizeEmail(identity.Destination);
        return await users.Users.SingleOrDefaultAsync(u => u.NormalizedEmail == email && u.EmailConfirmed, ct);
    }

    public async Task<IdentityUser> RegisterAsync(VerifiedIdentity identity, string? password, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var user = await RegisterCoreAsync(identity, password, ct);
        await transaction.CommitAsync(ct);
        return user;
    }

    private async Task<IdentityUser> RegisterCoreAsync(VerifiedIdentity identity, string? password, CancellationToken ct)
    {
        if (await DestinationInUse(identity, null, ct)) throw new AccountIdentityException("link_required", "Sign in to your existing account to link this identity.");
        var user = new IdentityUser { UserName = identity.Destination };
        if (identity.Channel == "email") { user.Email = identity.Destination; user.EmailConfirmed = true; }
        else { user.PhoneNumber = identity.Destination; user.PhoneNumberConfirmed = true; }
        var result = password is null ? await users.CreateAsync(user) : await users.CreateAsync(user, password);
        if (!result.Succeeded) throw new AccountIdentityException("registration_failed", "The account could not be created. Check your details and try again.");
        var installation = new Installation { Id = Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.UtcNow };
        db.Installations.Add(installation);
        db.InstallationMemberships.Add(new() { InstallationId = installation.Id, UserId = user.Id, Role = "Owner" });
        await db.SaveChangesAsync(ct);
        return user;
    }

    public async Task LinkAsync(string userId, VerifiedIdentity identity, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId) ?? throw new AccountIdentityException("link_failed", "Sign in again before linking an identity.");
        if (await DestinationInUse(identity, userId, ct)) throw new AccountIdentityException("link_conflict", "This identity already belongs to another account.");
        IdentityResult result;
        if (identity.Channel == "email")
        {
            // Linking adds or verifies the account email; silently replacing a different email is forbidden.
            if (user.EmailConfirmed && user.Email is not null && users.NormalizeEmail(user.Email) != users.NormalizeEmail(identity.Destination))
                throw new AccountIdentityException("link_conflict", "This account already has another email address.");
            user.Email = identity.Destination; user.EmailConfirmed = true;
            result = await users.UpdateAsync(user);
        }
        else
        {
            if (user.PhoneNumber is not null && user.PhoneNumber != identity.Destination)
                throw new AccountIdentityException("link_conflict", "This account already has another phone number.");
            user.PhoneNumber = identity.Destination; user.PhoneNumberConfirmed = true;
            result = await users.UpdateAsync(user);
        }
        if (!result.Succeeded) throw new AccountIdentityException("link_failed", "The identity could not be linked.");
    }

    public async Task<IdentityUser> GoogleAsync(string subject, string email, bool emailVerified, string? linkingUserId, CancellationToken ct)
    {
        var normalized = ValidateGoogle(subject, email, emailVerified);
        var existing = await users.FindByLoginAsync("Google", subject);
        if (existing is not null)
        {
            if (linkingUserId is not null && existing.Id != linkingUserId)
                throw new AccountIdentityException("link_conflict", "This Google identity already belongs to another account.");
            return existing;
        }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (linkingUserId is null)
        {
            if (await DestinationInUse(new("email", normalized), null, ct))
                throw new AccountIdentityException("link_required", "Sign in to your existing account to link Google.");
            existing = await RegisterCoreAsync(new("email", normalized), null, ct);
        }
        else existing = await users.FindByIdAsync(linkingUserId) ?? throw new AccountIdentityException("link_failed", "Sign in again before linking Google.");
        var result = await users.AddLoginAsync(existing, new UserLoginInfo("Google", subject, "Google"));
        if (!result.Succeeded) throw new AccountIdentityException("link_failed", "Google could not be linked to this account.");
        await transaction.CommitAsync(ct);
        return existing;
    }

    public static string ValidateGoogle(string subject, string email, bool emailVerified)
    {
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 255 || !emailVerified
            || !OneTimeVerificationService.TryNormalize("email", email, out var normalized))
            throw new AccountIdentityException("google_failed", "Google did not provide a verified identity.");
        return normalized;
    }

    private Task<bool> DestinationInUse(VerifiedIdentity identity, string? exceptUserId, CancellationToken ct)
        => identity.Channel == "phone"
            ? users.Users.AnyAsync(u => u.Id != exceptUserId && u.PhoneNumber == identity.Destination, ct)
            : users.Users.AnyAsync(u => u.Id != exceptUserId && u.NormalizedEmail == users.NormalizeEmail(identity.Destination), ct);
}

public sealed class AccountIdentityException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
