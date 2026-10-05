using DeyeSolar.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Integrations;

/// <summary>Uses the caller's tenant filters and transaction; it never opens an unguarded context.</summary>
internal static class IntegrationDeviceEligibility
{
    public static Task<IntegrationDeviceBindingEntity?> FindEnabledAsync(DeyeSolarDbContext db,
        Guid id, string kind, CancellationToken ct)
        => (from device in db.IntegrationDeviceBindings.AsNoTracking()
            join instance in db.IntegrationInstances.AsNoTracking() on device.InstanceId equals instance.Id
            where device.Id == id && device.Enabled && device.Kind == kind && instance.State == "enabled"
            select device).SingleOrDefaultAsync(ct);
}
