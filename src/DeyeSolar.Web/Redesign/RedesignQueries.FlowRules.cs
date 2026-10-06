using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Integrations;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Redesign;

public sealed partial class RedesignQueries
{
    public async Task<IReadOnlyList<TriggerRule>> FlowRulesAsync(IReadOnlyList<TriggerRule> installationRules,
        Guid? inverterId, CancellationToken ct = default)
    {
        await security.EnsureAsync(InstallationPermission.Read, ct);
        if (inverterId is null || installationRules.Count == 0) return [];
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await (from binding in db.IntegrationDeviceBindings
            join instance in db.IntegrationInstances on binding.InstanceId equals instance.Id
            where binding.Id == inverterId && binding.Kind == "inverter" && binding.Enabled && instance.State == "enabled"
            select binding.Id).AnyAsync(ct)) return [];
        var sources = await IntegrationSocketAssociation.ResolveSourcesAsync(db, installationRules, ct);
        var primary = await (from binding in db.IntegrationDeviceBindings
            join instance in db.IntegrationInstances on binding.InstanceId equals instance.Id
            where binding.Kind == "inverter" && binding.Enabled && binding.IsDefault && instance.State == "enabled"
            select (Guid?)binding.Id).SingleOrDefaultAsync(ct);
        return installationRules.Where(rule => rule.Enabled && (sources.GetValueOrDefault(rule.Id) ?? primary) == inverterId).ToArray();
    }
}
