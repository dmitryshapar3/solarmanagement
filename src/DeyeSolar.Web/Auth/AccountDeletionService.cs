using System.Data;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
namespace DeyeSolar.Web.Auth;

/// <summary>Fences installation admission, drains work, then deletes only exclusively owned data.</summary>
public sealed class AccountDeletionService(DbContextOptions<DeyeSolarDbContext> database, TenantRuntimeRegistry runtimes,
    AppleIdentityCredentialStore? apple = null)
{
    public async Task DeleteAsync(IdentityUser user, CancellationToken ct)
    {
        await using var db = new DeyeSolarDbContext(database);
        string[] owned;
        Dictionary<string, bool> enabled;
        await using (var admission = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct))
        {
            owned = await OwnedAsync(db, user.Id, ct);
            await CheckConflictsAsync(db, user.Id, owned, ct);
            enabled = await db.Installations.Where(i => owned.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.IsEnabled, ct);
            // Commands lock this same installation before accepting new intent. Keep the admission
            // fence until commit, so the final post-drain check observes every accepted command.
            if (await db.Installations.AnyAsync(i => owned.Contains(i.Id) && i.OffboardingUserId != null, ct))
                throw new AccountSecurityException("delete_in_progress", "The request could not be completed. Please try again later.");
            await db.Installations.Where(i => owned.Contains(i.Id)).ExecuteUpdateAsync(s => s
                .SetProperty(i => i.OffboardingWasEnabled, i => (bool?)i.IsEnabled)
                .SetProperty(i => i.OffboardingUserId, user.Id).SetProperty(i => i.IsEnabled, false), ct);
            await admission.CommitAsync(ct);
        }
        var committed = false;
        try
        {
            foreach (var id in owned) await runtimes.PauseInstallationAsync(id, ct);
            await using var deletion = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            // Lock installation parents in the same order used by command admission.
            foreach (var id in owned.Order(StringComparer.Ordinal))
                if (db.Database.IsSqlServer())
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT [Id] FROM [Installations] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}", ct);
            if (!await db.Users.AnyAsync(u => u.Id == user.Id && u.SecurityStamp == user.SecurityStamp, ct))
                throw new AccountSecurityException("session_invalid", "Sign in again.", 401);
            var currentOwned = await OwnedAsync(db, user.Id, ct);
            if (!currentOwned.Order(StringComparer.Ordinal).SequenceEqual(owned.Order(StringComparer.Ordinal)))
                throw new AccountSecurityException("ownership_changed", "Installation ownership changed. Review your account before deleting it.");
            await CheckConflictsAsync(db, user.Id, owned, ct);
            await db.IntegrationOAuthFlows.IgnoreQueryFilters().Where(f => owned.Contains(f.InstallationId) || f.UserId == user.Id).ExecuteDeleteAsync(ct);
            await db.IntegrationCommands.IgnoreQueryFilters().Where(c => owned.Contains(c.InstallationId)).ExecuteDeleteAsync(ct);
            await db.IntegrationDeviceBindings.IgnoreQueryFilters().Where(b => owned.Contains(b.InstallationId)).ExecuteDeleteAsync(ct);
            await db.IntegrationConfigurations.IgnoreQueryFilters().Where(c => owned.Contains(c.InstallationId)).ExecuteDeleteAsync(ct);
            await db.IntegrationInstances.IgnoreQueryFilters().Where(i => owned.Contains(i.InstallationId)).ExecuteDeleteAsync(ct);
            await db.TriggerRules.IgnoreQueryFilters().Where(r => owned.Contains(r.InstallationId)).ExecuteDeleteAsync(ct);
            await db.RuleRunLogs.IgnoreQueryFilters().Where(r => owned.Contains(r.InstallationId)).ExecuteDeleteAsync(ct);
            await db.ActivityEvents.IgnoreQueryFilters().Where(r => owned.Contains(r.InstallationId)).ExecuteDeleteAsync(ct);
            await db.Readings.IgnoreQueryFilters().Where(r => owned.Contains(r.InstallationId)).ExecuteDeleteAsync(ct);
            await db.ExportReadings.IgnoreQueryFilters().Where(r => owned.Contains(r.InstallationId)).ExecuteDeleteAsync(ct);
            await db.AppSettings.IgnoreQueryFilters().Where(r => owned.Contains(r.InstallationId)).ExecuteDeleteAsync(ct);
            await db.InstallationMemberships.Where(m => m.UserId == user.Id).ExecuteDeleteAsync(ct);
            await db.Installations.Where(i => owned.Contains(i.Id)).ExecuteDeleteAsync(ct);
            // Identity, billing, subscriptions and sessions have user cascade FKs. The removal
            // revokes browser circuits and bearer sessions atomically with the account deletion.
            if (apple is not null) await apple.QueueRevokeAsync(db, user.Id, ct);
            await db.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync(ct);
            await deletion.CommitAsync(ct);
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                // Failure or caller cancellation does not strand a recoverable installation.
                foreach (var id in owned) await runtimes.PauseInstallationAsync(id, CancellationToken.None);
                await using var restore = new DeyeSolarDbContext(database);
                foreach (var (id, wasEnabled) in enabled)
                    await restore.Installations.Where(i => i.Id == id && i.OffboardingUserId == user.Id).ExecuteUpdateAsync(s => s
                        .SetProperty(i => i.IsEnabled, wasEnabled).SetProperty(i => i.OffboardingUserId, (string?)null)
                        .SetProperty(i => i.OffboardingWasEnabled, (bool?)null), CancellationToken.None);
                foreach (var id in owned) runtimes.ResumeInstallation(id);
            }
        }
        foreach (var id in owned) await runtimes.StopInstallationAsync(id, CancellationToken.None);
    }
    private static Task<string[]> OwnedAsync(DeyeSolarDbContext db, string userId, CancellationToken ct)
        => db.InstallationMemberships.Where(m => m.UserId == userId && m.Role == "Owner").Select(m => m.InstallationId).ToArrayAsync(ct);
    private static async Task CheckConflictsAsync(DeyeSolarDbContext db, string userId, string[] owned, CancellationToken ct)
    {
        if (await db.InstallationMemberships.AnyAsync(m => owned.Contains(m.InstallationId) && m.UserId != userId, ct))
            throw new AccountSecurityException("ownership_transfer_required", "Transfer ownership of shared installations before deleting this account.");
        if (await db.IntegrationCommands.IgnoreQueryFilters().AnyAsync(c => owned.Contains(c.InstallationId)
            && (c.Status == "requested" || c.Status == "pending" || c.Status == "uncertain"), ct))
            throw new AccountSecurityException("unresolved_commands", "Resolve outstanding device commands before deleting this account.");
    }
}
