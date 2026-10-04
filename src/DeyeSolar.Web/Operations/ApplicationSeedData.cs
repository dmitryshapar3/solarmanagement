using DeyeSolar.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Operations;

public interface IApplicationSeedData { Task SeedAsync(CancellationToken ct); }

/// <summary>Explicit operator provisioning creates one account and its own installation atomically.</summary>
public sealed class ApplicationSeedData(DeyeSolarDbContext identityDb, UserManager<IdentityUser> users,
    DeploymentConfiguration deployment, TimeProvider clock, ILogger<ApplicationSeedData> logger) : IApplicationSeedData
{
    public async Task SeedAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deployment.BootstrapAdminPassword)
            || await users.FindByNameAsync("admin") is not null) return;

        var installation = new Installation { Id = Guid.NewGuid().ToString("N"), CreatedAt = clock.GetUtcNow() };
        // An operator explicitly provisions this account, rather than registering an unverified contact.
        var adminUser = new IdentityUser { UserName = "admin", Email = "admin@deye.local", EmailConfirmed = true };
        await using var transaction = await identityDb.Database.BeginTransactionAsync(ct);
        identityDb.Installations.Add(installation);
        var result = await users.CreateAsync(adminUser, deployment.BootstrapAdminPassword);
        if (!result.Succeeded)
            throw new InvalidOperationException("The bootstrap administrator could not be created. Check Auth:BootstrapAdminPassword requirements.");
        identityDb.InstallationMemberships.Add(new InstallationMembership
        {
            InstallationId = installation.Id, UserId = adminUser.Id, Role = "Owner"
        });
        await identityDb.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        logger.LogInformation("Bootstrap administrator created. Credentials are not logged.");
    }
}
