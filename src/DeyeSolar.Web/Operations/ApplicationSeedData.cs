using DeyeSolar.Domain.Options;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Integrations;
using DeyeSolar.Web.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Operations;

public interface IApplicationSeedData { Task SeedAsync(CancellationToken ct); }

public sealed class ApplicationSeedData(DbContextOptions<DeyeSolarDbContext> databaseOptions, DeyeSolarDbContext identityDb,
    UserManager<IdentityUser> users, LegacyIntegrationBootstrap legacy, DeploymentConfiguration deployment,
    ILogger<ApplicationSeedData> logger) : IApplicationSeedData
{
    public async Task SeedAsync(CancellationToken ct)
    {
        var dbFactory = new TenantDbContextFactory(databaseOptions, InstallationIds.Legacy);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.Installations.AnyAsync(i => i.Id == InstallationIds.Legacy, ct))
        {
            db.Installations.Add(new Installation { Id = InstallationIds.Legacy, Name = "Existing installation", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(ct);
        }

        // Seed settings
        var settingsService = new AppSettingsService(dbFactory, deployment.Configuration);
        if (!await legacy.RunAsync(dbFactory, deployment.Configuration, ct))
            logger.LogWarning("Legacy connections await trusted provider packages. Existing credentials have been preserved for import.");
        await settingsService.SeedSectionAsync<PollingOptions>(PollingOptions.Section);
        await settingsService.SeedSectionAsync<DisplayOptions>(DisplayOptions.Section);

        // Seed default rule
        if (!await db.TriggerRules.AnyAsync(ct))
        {
            db.TriggerRules.Add(new DeyeSolar.Domain.Models.TriggerRule
            {
                Name = "Solar Surplus Diverter",
                EntityId = "",
                Enabled = false,
                SocTurnOnThreshold = 80,
                UseSeparateSocTurnOffThreshold = false,
                SocTurnOffThreshold = 80,
                UseSolarProductionThreshold = false,
                MinAverageSolarProductionWatts = 3000,
                CooldownMinutes = 15,
                IntervalSeconds = 30
            });
            await db.SaveChangesAsync(ct);
        }

        // Seed admin user
        var adminUser = await users.FindByNameAsync("admin");
        if (adminUser == null && !string.IsNullOrWhiteSpace(deployment.BootstrapAdminPassword))
        {
            adminUser = new IdentityUser { UserName = "admin", Email = "admin@deye.local" };
            await using var transaction = await identityDb.Database.BeginTransactionAsync(ct);
            var result = await users.CreateAsync(adminUser, deployment.BootstrapAdminPassword);
            if (result.Succeeded)
            {
                identityDb.InstallationMemberships.Add(new InstallationMembership { InstallationId = InstallationIds.Legacy, UserId = adminUser.Id, Role = "Owner" });
                await identityDb.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                logger.LogInformation("Bootstrap administrator created. Credentials are not logged.");
            }
            else throw new InvalidOperationException("The bootstrap administrator could not be created. Check Auth:BootstrapAdminPassword requirements.");
        }
    }
}
