using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Tests;

public class TenantDataIsolationTests
{
    private static readonly DateTime At = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private sealed class Database : IAsyncDisposable
    {
        public readonly SqliteConnection Connection = new("Data Source=:memory:");
        public DbContextOptions<DeyeSolarDbContext> Options => new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlite(Connection).Options;
        public DeyeSolarDbContext Context(string? installation = null) => installation is null ? new(Options) : new(Options, installation);
        public async Task InitializeAsync()
        {
            await Connection.OpenAsync(); Connection.CreateCollation("Latin1_General_100_BIN2", string.CompareOrdinal);
            await using var global = Context(); await global.Database.EnsureCreatedAsync();
            global.Installations.AddRange(new Installation { Id = "a", CreatedAt = DateTimeOffset.UtcNow }, new Installation { Id = "b", CreatedAt = DateTimeOffset.UtcNow });
            global.Users.Add(new IdentityUser { Id = "global-user", UserName = "public identity" });
            global.ExportPrices.Add(new ExportPriceRow { StartUtc = At, PricePlnPerMwh = 123.456789m });
            await global.SaveChangesAsync();
        }
        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }

    [Fact]
    public async Task AllPrivateQueriesAreIsolatedWhilePublicMarketAndIdentityRemainGlobal()
    {
        await using var database = new Database(); await database.InitializeAsync();
        foreach (var installation in new[] { "a", "b" })
        {
            await using var own = database.Context(installation);
            own.Readings.Add(new Reading { Timestamp = At, DataSource = installation });
            own.ExportReadings.Add(new ExportReading { DeviceSn = "same-device", ObservedAt = At, PolledAt = At, GridPowerWatts = installation == "a" ? 1 : 2 });
            own.TriggerRules.Add(new TriggerRule { Name = installation, EntityId = "same-socket" });
            own.AppSettings.Add(new AppSetting { Section = "DeyeCloud", Key = "Password", Value = installation });
            own.RuleRunLogs.Add(new RuleRunLog { Timestamp = At, RuleName = installation });
            await own.SaveChangesAsync();
            Assert.All(own.ChangeTracker.Entries<DeyeSolar.Domain.Models.IInstallationOwned>(), entry => Assert.Equal(installation, entry.Entity.InstallationId));
        }
        foreach (var installation in new[] { "a", "b" })
        {
            await using var own = database.Context(installation);
            Assert.Equal(installation, (await own.Readings.SingleAsync()).DataSource);
            Assert.Equal(installation, (await own.TriggerRules.SingleAsync()).Name);
            Assert.Equal(installation, (await own.AppSettings.SingleAsync()).Value);
            Assert.Equal(installation, (await own.RuleRunLogs.SingleAsync()).RuleName);
            Assert.Equal(installation == "a" ? 1 : 2, (await own.ExportReadings.SingleAsync()).GridPowerWatts);
        }
        await using var unbound = database.Context();
        Assert.Empty(await unbound.Readings.ToListAsync()); Assert.Empty(await unbound.ExportReadings.ToListAsync());
        Assert.Empty(await unbound.TriggerRules.ToListAsync()); Assert.Empty(await unbound.AppSettings.ToListAsync()); Assert.Empty(await unbound.RuleRunLogs.ToListAsync());
        Assert.Single(await unbound.ExportPrices.ToListAsync()); Assert.Single(await unbound.Users.ToListAsync());
    }

    [Fact]
    public async Task UnboundWritesAndForeignExplicitRowsAreDenied()
    {
        await using var database = new Database(); await database.InitializeAsync();
        await using var unbound = database.Context(); unbound.AppSettings.Add(new AppSetting { Section = "secret", Key = "value", Value = "blocked" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => unbound.SaveChangesAsync());
        await using var own = database.Context("a"); own.Readings.Add(new Reading { InstallationId = "b", Timestamp = At });
        await Assert.ThrowsAsync<InvalidOperationException>(() => own.SaveChangesAsync());
        own.ChangeTracker.Clear(); own.AppSettings.Add(new AppSetting { Section = "safe", Key = "value", Value = "saved" });
        own.SaveChanges();
        Assert.Equal("a", (await own.AppSettings.SingleAsync()).InstallationId);
    }

    [Fact]
    public async Task DisconnectedForeignUpdatesAndDeletesCannotSpoofTenantId()
    {
        await using var database = new Database(); await database.InitializeAsync(); int id;
        await using (var a = database.Context("a"))
        {
            var row = new AppSetting { Section = "DeyeCloud", Key = "Password", Value = "original" };
            a.AppSettings.Add(row); await a.SaveChangesAsync(); id = row.Id;
        }
        await using (var b = database.Context("b"))
        {
            b.AppSettings.Update(new AppSetting { Id = id, InstallationId = "b", Section = "DeyeCloud", Key = "Password", Value = "attacker" });
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => b.SaveChangesAsync());
        }
        await using (var b = database.Context("b"))
        {
            b.AppSettings.Remove(new AppSetting { Id = id, InstallationId = "b" });
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => b.SaveChangesAsync());
        }
        await using var check = database.Context("a"); Assert.Equal("original", (await check.AppSettings.SingleAsync()).Value);
    }

    [Fact]
    public async Task ChangingInstallationOfTrackedRowIsRejected()
    {
        await using var database = new Database(); await database.InitializeAsync();
        await using var a = database.Context("a"); a.Readings.Add(new Reading { Timestamp = At }); await a.SaveChangesAsync();
        var row = await a.Readings.SingleAsync(); row.InstallationId = "b";
        await Assert.ThrowsAsync<InvalidOperationException>(() => a.SaveChangesAsync());
    }

    [Fact]
    public void RequestBindingIsImmutableAndHasNoLegacyFallback()
    {
        var installation = new CurrentInstallation(); Assert.Null(installation.Id);
        Assert.Throws<ArgumentException>(() => installation.BindOnce(""));
        installation.BindOnce("a"); installation.BindOnce("a");
        Assert.Throws<InvalidOperationException>(() => installation.BindOnce("b")); Assert.Equal("a", installation.Id);
    }
}
