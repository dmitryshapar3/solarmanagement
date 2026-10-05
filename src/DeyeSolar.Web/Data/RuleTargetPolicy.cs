using DeyeSolar.Domain.Models;
using Microsoft.EntityFrameworkCore;
using DeyeSolar.Web.Integrations;

namespace DeyeSolar.Web.Data;

/// <summary>Serializes target admission and compares canonical device identities.</summary>
internal static class RuleTargetPolicy
{
    public static Task LockInstallationAsync(DeyeSolarDbContext db, CancellationToken ct)
        => DeyeSolar.Web.Integrations.InstallationMutationGuard.EnsureEnabledAsync(db, ct);

    public static Task<string> IdentityAsync(DeyeSolarDbContext db, string target, CancellationToken ct)
        => Task.FromResult(Guid.TryParse(target, out var id) ? id.ToString("D") : target.Trim());

    public static async Task ValidateAsync(DeyeSolarDbContext db, TriggerRule rule, CancellationToken ct)
    {
        rule.EntityId = await IdentityAsync(db, rule.EntityId, ct);
        // A disabled draft may retain a retired selection, but enabling it must admit a
        // current switchable socket through this installation's filtered device registry.
        if (!rule.Enabled) return;
        if (!Guid.TryParse(rule.EntityId, out var id) || id == Guid.Empty)
            throw new ArgumentException("Choose an available socket from this installation.");
        var device = await IntegrationDeviceEligibility.FindEnabledAsync(db, id, "socket", ct);
        if (device is null || !IntegrationCapabilities.ReadSocket(device).CanSwitch)
            throw new ArgumentException("Choose an available socket from this installation.");
        var targets = await db.TriggerRules.AsNoTracking().Where(r => r.Enabled && r.Id != rule.Id)
            .Select(r => r.EntityId).ToListAsync(ct);
        foreach (var target in targets)
            if (await IdentityAsync(db, target, ct) == rule.EntityId)
                throw new ArgumentException("This socket already has an enabled automation rule. Disable it before enabling another rule.");
    }

}
