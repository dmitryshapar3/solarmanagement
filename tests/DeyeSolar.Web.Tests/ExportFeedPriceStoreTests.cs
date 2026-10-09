using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Services;
using DeyeSolar.Web.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Tests;

public sealed class ExportFeedPriceStoreTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-09T00:00:00Z");
    [SqlServerFact]
    public async Task AdditiveMigrationCachesFeedByInstallationAndSourceWithoutReplacingOfficialMarketData()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync("SolarPrivateFeed", seed: TestInstallation.EnsureAsync);
        await using (var admin = new DeyeSolarDbContext(database.Options))
        {
            admin.Installations.Add(new() { Id = "other-installation", CreatedAt = Start }); await admin.SaveChangesAsync();
        }
        var clock = new Clock(); var first = new ExportPriceStore(database.Factory, clock);
        var second = new ExportPriceStore(new TenantDbContextFactory(database.Options, "other-installation"), clock);
        var sourceA = new string('a', 64); var sourceB = new string('b', 64);
        ExportPriceInterval Price(decimal value) => new(Start, Start.AddMinutes(15), value);
        await first.SaveAsync([Price(900m)], Start, default);
        await first.SaveFeedAsync(sourceA, [Price(-12.123456m)], Start, default);
        await first.SaveFeedAsync(sourceB, [Price(300m)], Start, default);
        await second.SaveFeedAsync(sourceA, [Price(400m)], Start, default);
        await first.SaveFeedAsync(sourceA, [Price(200m)], Start.AddMinutes(1), default);
        await first.SaveFeedAsync(sourceA, [Price(700m)], Start, default);
        Assert.Equal(200m, Assert.Single(await first.ReadFeedAsync(sourceA, Start, Start.AddHours(1), default)).PricePlnPerMwh);
        Assert.Equal(300m, Assert.Single(await first.ReadFeedAsync(sourceB, Start, Start.AddHours(1), default)).PricePlnPerMwh);
        Assert.Equal(400m, Assert.Single(await second.ReadFeedAsync(sourceA, Start, Start.AddHours(1), default)).PricePlnPerMwh);
        Assert.Equal(900m, Assert.Single(await first.ReadAsync(Start, Start.AddHours(1), default)).PricePlnPerMwh);
        await using var check = database.Factory.CreateDbContext();
        Assert.Equal(2, await check.ExportFeedPrices.CountAsync()); Assert.Equal(3, await check.ExportFeedPrices.IgnoreQueryFilters().CountAsync());
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Start.AddDays(1); }
}
