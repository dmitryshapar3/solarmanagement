using DeyeSolar.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Data;

/// <summary>Serializes target admission and compares aliases by physical device identity.</summary>
internal static class RuleTargetPolicy
{
    public static Task LockInstallationAsync(DeyeSolarDbContext db, CancellationToken ct)
        => DeyeSolar.Web.Integrations.InstallationMutationGuard.EnsureEnabledAsync(db, ct);

    public static async Task<string> IdentityAsync(DeyeSolarDbContext db, string target, CancellationToken ct)
    {
        if (Guid.TryParse(target, out var id)) return id.ToString("D");
        var alias = await db.IntegrationDeviceAliases.AsNoTracking().SingleOrDefaultAsync(a => a.LegacyId == target, ct);
        return alias?.DeviceId.ToString("D") ?? target.Trim();
    }

    public static async Task ValidateAsync(DeyeSolarDbContext db, TriggerRule rule, CancellationToken ct)
    {
        rule.EntityId = await IdentityAsync(db, rule.EntityId, ct);
        if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.EntityId)) return;
        var targets = await db.TriggerRules.AsNoTracking().Where(r => r.Enabled && r.Id != rule.Id)
            .Select(r => r.EntityId).ToListAsync(ct);
        foreach (var target in targets)
            if (await IdentityAsync(db, target, ct) == rule.EntityId)
                throw new ArgumentException("This socket already has an enabled automation rule. Disable it before enabling another rule.");
    }
}
