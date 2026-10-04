using DeyeSolar.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Integrations;

public interface IIntegrationConnectionLifecycle
{
    void Touch(IntegrationInstanceEntity instance);
    Task<bool> SetEnabledAsync(DeyeSolarDbContext db, IntegrationInstanceEntity instance, bool enabled, CancellationToken ct);
}

/// <summary>Owns connection generation changes and retirement of effects that can no longer be authenticated.</summary>
public sealed class IntegrationConnectionLifecycle(TimeProvider clock) : IIntegrationConnectionLifecycle
{
    public void Touch(IntegrationInstanceEntity instance) { instance.Generation++; instance.UpdatedAt = clock.GetUtcNow(); }
    public async Task<bool> SetEnabledAsync(DeyeSolarDbContext db, IntegrationInstanceEntity instance, bool enabled, CancellationToken ct)
    {
        var desired = enabled ? "enabled" : "disabled";
        var changed = instance.State != desired;
        var retired = 0;
        if (!enabled)
            retired = await db.IntegrationCommands.Where(command => command.InstanceId == instance.Id && (command.Status == "requested" || command.Status == "pending"))
                .ExecuteUpdateAsync(update => update.SetProperty(command => command.Status, "uncertain")
                    .SetProperty(command => command.ErrorCode, "retired_generation")
                    .SetProperty(command => command.CompletedAt, clock.GetUtcNow()), ct);
        if (changed) { instance.State = desired; Touch(instance); }
        return changed || retired > 0;
    }
}
