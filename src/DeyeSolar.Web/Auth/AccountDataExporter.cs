using DeyeSolar.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
namespace DeyeSolar.Web.Auth;
public sealed class AccountDataExporter(DbContextOptions<DeyeSolarDbContext> database, TimeProvider clock)
{
    public async Task<object> ExportAsync(IdentityUser user, CancellationToken ct)
    {
        await using var db = new DeyeSolarDbContext(database);
        var memberships = await db.InstallationMemberships.AsNoTracking().Where(m => m.UserId == user.Id).ToListAsync(ct);
        var owned = memberships.Where(m => m.Role == "Owner").Select(m => m.InstallationId).ToArray();
        // Export contains account/profile and tenant configuration/history, never hashes, sessions or provider credentials.
        return new { exportedAt = clock.GetUtcNow(), account = new { user.Id, user.UserName, user.Email, user.PhoneNumber },
            memberships = memberships.Select(m => new { m.InstallationId, m.Role }),
            billing = await db.BillingAccounts.AsNoTracking().SingleOrDefaultAsync(b => b.UserId == user.Id, ct),
            subscriptions = await db.AppleSubscriptions.AsNoTracking().Where(s => s.UserId == user.Id).ToListAsync(ct),
            installations = await db.Installations.AsNoTracking().Where(i => owned.Contains(i.Id)).ToListAsync(ct),
            rules = await db.TriggerRules.IgnoreQueryFilters().AsNoTracking().Where(r => owned.Contains(r.InstallationId)).ToListAsync(ct),
            readings = await db.Readings.IgnoreQueryFilters().AsNoTracking().Where(r => owned.Contains(r.InstallationId)).ToListAsync(ct),
            exportFeedPrices = await db.ExportFeedPrices.IgnoreQueryFilters().AsNoTracking().Where(r => owned.Contains(r.InstallationId)).ToListAsync(ct),
            exportReadings = await db.ExportReadings.IgnoreQueryFilters().AsNoTracking().Where(r => owned.Contains(r.InstallationId)).ToListAsync(ct) };
    }
}
