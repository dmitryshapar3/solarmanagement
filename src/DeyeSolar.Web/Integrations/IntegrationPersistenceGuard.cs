using DeyeSolar.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Integrations;

internal static class IntegrationPersistenceGuard
{
    public static async Task<IntegrationInstanceEntity?> LockInstanceAsync(DeyeSolarDbContext db, Guid instanceId, CancellationToken ct)
    {
        if (!db.Database.IsSqlServer())
            return await db.IntegrationInstances.AsNoTracking().SingleOrDefaultAsync(instance => instance.Id == instanceId, ct);
        var installation = db.InstallationId ?? throw new InvalidOperationException("An installation is required.");
        return await db.IntegrationInstances.FromSqlInterpolated($"SELECT * FROM [IntegrationInstances] WITH (UPDLOCK, HOLDLOCK) WHERE [InstallationId] = {installation} AND [Id] = {instanceId}")
            .AsNoTracking().SingleOrDefaultAsync(ct);
    }
    // The caller's transaction holds this lock through its local side effects. Settings changes
    // update the same parent row, so a retired generation cannot win a read/check/write race.
    public static async Task<bool> LockCurrentAsync(DeyeSolarDbContext db, Guid deviceId,
        long revision, long generation, CancellationToken ct)
    {
        var binding = await db.IntegrationDeviceBindings.AsNoTracking()
            .SingleOrDefaultAsync(b => b.Id == deviceId && b.Enabled, ct);
        if (binding is null) return false;
        var instance = await LockInstanceAsync(db, binding.InstanceId, ct);
        return instance is { State: "enabled" } && instance.Revision == revision && instance.Generation == generation;
    }
}
