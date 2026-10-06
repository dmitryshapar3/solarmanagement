using DeyeSolar.Web.Auth;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Redesign;

public sealed partial class RedesignQueries
{
    public async Task<string?> ReadingsProviderAsync(Guid? inverterId, CancellationToken ct = default)
    {
        await security.EnsureAsync(InstallationPermission.Read, ct);
        if (inverterId is null || catalog is null) return null;
        await using var db = await factory.CreateDbContextAsync(ct);
        var instance = await (from binding in db.IntegrationDeviceBindings.AsNoTracking()
            join integration in db.IntegrationInstances.AsNoTracking() on binding.InstanceId equals integration.Id
            where binding.Id == inverterId && binding.Kind == "inverter" && binding.Enabled && integration.State == "enabled"
            select integration).SingleOrDefaultAsync(ct);
        if (instance is null) return null;
        try
        {
            var descriptor = await catalog.GetAsync(instance.ProviderId, instance.PackageVersion, ct);
            return descriptor.PackageDigest == instance.PackageDigest && descriptor.DescriptorDigest == instance.DescriptorDigest
                ? descriptor.DisplayName : null;
        }
        catch (InvalidOperationException) { return null; }
    }
}
