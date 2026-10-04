using DeyeSolar.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Tests;

public class SolarDataCommandCounterTests
{
    [Fact]
    public async Task IdentityAndMembershipChecksAreExcludedWhileSolarReadsAndWritesAreCounted()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        connection.CreateCollation("Latin1_General_100_BIN2", string.CompareOrdinal);
        var counter = new SolarDataCommandCounter();
        var options = new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlite(connection).AddInterceptors(counter).Options;
        await using var db = new DeyeSolarDbContext(options, TestInstallation.Id); await db.Database.EnsureCreatedAsync();
        db.Installations.Add(new Installation { Id = TestInstallation.Id, CreatedAt = DateTimeOffset.UtcNow });
        var user = new IdentityUser { Id = "test-user", UserName = "reader" }; db.Users.Add(user);
        db.InstallationMemberships.Add(new InstallationMembership { UserId = user.Id, InstallationId = TestInstallation.Id }); await db.SaveChangesAsync();
        var before = counter.Commands;
        Assert.Single(await db.Users.AsNoTracking().ToListAsync());
        Assert.Single(await db.InstallationMemberships.AsNoTracking().Include(m => m.Installation).ToListAsync());
        Assert.Equal(before, counter.Commands);
        Assert.Empty(await db.Readings.AsNoTracking().ToListAsync()); Assert.Equal(before + 1, counter.Commands);
        db.AppSettings.Add(new AppSetting { Section = "Display", Key = "TimeZoneId", Value = "UTC" }); await db.SaveChangesAsync();
        Assert.Equal(before + 2, counter.Commands);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM AppSettings"); Assert.Equal(before + 3, counter.Commands);
        Assert.Empty(await db.ExportPrices.AsNoTracking().ToListAsync()); Assert.Equal(before + 4, counter.Commands);
    }
}
