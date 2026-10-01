using DeyeSolar.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Tests;

internal static class LegacyTestInstallation
{
    internal static async Task EnsureAsync(DeyeSolarDbContext db)
    {
        if (await db.Installations.AnyAsync(i => i.Id == InstallationIds.Legacy)) return;
        db.Installations.Add(new Installation { Id = InstallationIds.Legacy, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }
}
