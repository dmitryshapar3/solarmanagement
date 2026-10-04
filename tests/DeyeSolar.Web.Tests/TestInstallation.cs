using DeyeSolar.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Tests;

internal static class TestInstallation
{
    internal const string Id = "fixture-installation";
    internal static async Task EnsureAsync(DeyeSolarDbContext db)
    {
        if (await db.Installations.AnyAsync(i => i.Id == TestInstallation.Id)) return;
        db.Installations.Add(new Installation { Id = TestInstallation.Id, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }
}
