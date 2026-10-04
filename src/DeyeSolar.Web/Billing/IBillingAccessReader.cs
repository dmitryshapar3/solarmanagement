using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DeyeSolar.Web.Billing;

public interface IBillingAccessReader
{
    Task<BillingAccess> ReadAsync(string userId, CancellationToken ct = default);
    Task EnsureUserAsync(ClaimsPrincipal actor, CancellationToken ct);
    Task<bool> InstallationHasAccessAsync(string installationId, CancellationToken ct);
    Task EnsureInstallationAsync(string installationId, CancellationToken ct);
}

public static class BillingAccessRegistration
{
    public static IServiceCollection AddBillingAccess(this IServiceCollection services)
    {
        services.TryAddSingleton<BillingAccessService>();
        services.TryAddSingleton<IBillingAccessReader>(provider => provider.GetRequiredService<BillingAccessService>());
        services.TryAddSingleton<ITrialSocketQuota, TrialSocketQuota>();
        return services;
    }
}
