using DeyeSolar.Web.Data;
using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Integrations;
using DeyeSolar.Web.Tenancy;
using Microsoft.EntityFrameworkCore;
using SolarManagement.Integrations.Contracts;
using SolarManagement.Integrations.Runtime;

namespace DeyeSolar.Web.Operations;

public interface IApplicationDatabaseInitializer
{
    Task InitializeAsync(CancellationToken ct);
}

public sealed class ApplicationDatabaseInitializer(IServiceScopeFactory scopes, DeploymentConfiguration deployment) : IApplicationDatabaseInitializer
{
    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        // Verify all configured signed artifacts before a billing migration starts existing trial clocks.
        await scope.ServiceProvider.GetRequiredService<IntegrationPackageBootstrap>().EnsureInstalledAsync(ct);
        await scope.ServiceProvider.GetRequiredService<IIntegrationProviderCatalog>().GetProvidersAsync(ct);
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<DeyeSolarDbContext>>();
        var dbFactory = new TenantDbContextFactory(options, InstallationIds.Legacy);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (deployment.DatabaseMode == DatabaseStartupMode.Validate)
        {
            await DatabaseSchemaVerifier.VerifyAsync(db, deployment.RequireLeastPrivilege, ct);
            await VerifyPinnedPackagesAsync(db, scope.ServiceProvider.GetRequiredService<IIntegrationPackageManager>(), ct);
            await scope.ServiceProvider.GetRequiredService<AccountOffboardingRecovery>().RecoverInterruptedAsync(ct);
            return;
        }
        if (await db.Database.CanConnectAsync(ct))
        {
            // Reject an older migration image and verify versions already pinned by installations before cutover.
            await DatabaseSchemaVerifier.EnsureCompatibleMigrationHistoryAsync(db, ct);
            var hasInstances = await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name = 'IntegrationInstances' AND schema_id = SCHEMA_ID('dbo')").SingleAsync(ct);
            if (hasInstances != 0)
                await VerifyPinnedPackagesAsync(db, scope.ServiceProvider.GetRequiredService<IIntegrationPackageManager>(), ct);
        }
        await db.Database.MigrateAsync(ct);
        await DatabaseSchemaVerifier.VerifyAsync(db, false, ct);
        await scope.ServiceProvider.GetRequiredService<AccountOffboardingRecovery>().RecoverInterruptedAsync(ct);
        await scope.ServiceProvider.GetRequiredService<IApplicationSeedData>().SeedAsync(ct);
        await RuntimeDatabaseProvisioner.ProvisionAsync(db, deployment.Configuration, ct);
        await DatabaseSchemaVerifier.VerifyAsync(db, false, ct);
    }
    private static async Task VerifyPinnedPackagesAsync(DeyeSolarDbContext db, IIntegrationPackageManager packages, CancellationToken ct)
    {
        var identities = await db.IntegrationInstances.IgnoreQueryFilters().AsNoTracking()
            .Select(instance => new { instance.ProviderId, instance.PackageVersion, instance.PackageDigest }).Distinct().ToListAsync(ct);
        foreach (var identity in identities)
            await packages.ResolveAsync(new(identity.ProviderId, identity.PackageVersion, identity.PackageDigest), ct);
    }

}
