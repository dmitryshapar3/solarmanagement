using DeyeSolar.Web.Data;
using DeyeSolar.Domain.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;

namespace DeyeSolar.Web.Tests;

public sealed class SettingsTransactionTests
{
    [SqlServerFact]
    public async Task FullAndPartialSiteWritesCannotPersistComputedAliasesOrReplaceTheOperatorWeatherKey()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync("SolarSettingsSchema", seed: TestInstallation.EnsureAsync);
        await using var db = database.Factory.CreateDbContext();
        db.AppSettings.Add(new() { Section = SolarEstimateOptions.Section, Key = nameof(SolarEstimateOptions.ApiKey), Value = "old-stored-key" });
        await db.SaveChangesAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["SolarEstimate:ApiKey"] = "operator-weather-key" }).Build();
        var settings = new AppSettingsService(database.Factory, configuration);
        await settings.SaveSectionAsync(SolarEstimateOptions.Section, new SolarEstimateOptions { Roof1Kwp = 5.125, ApiKey = "attempted-replacement" });
        await settings.SaveSectionAsync(SolarEstimateOptions.Section, new { Roof1Kwp = 6.5, ApiKey = "second-attempt" });
        var stored = await db.AppSettings.AsNoTracking().Where(setting => setting.Section == SolarEstimateOptions.Section)
            .ToDictionaryAsync(setting => setting.Key, setting => setting.Value);
        Assert.Equal("old-stored-key", stored[nameof(SolarEstimateOptions.ApiKey)]);
        Assert.Equal("6.5", stored[nameof(SolarEstimateOptions.Roof1Kwp)]);
        Assert.DoesNotContain(nameof(SolarEstimateOptions.TotalKwp), stored.Keys);
        Assert.DoesNotContain(nameof(SolarEstimateOptions.SolarPowerIsPvDcConfirmed), stored.Keys);
        Assert.DoesNotContain(nameof(SolarEstimateOptions.ConfirmedInverterKey), stored.Keys);
        var loaded = await settings.LoadSectionAsync<SolarEstimateOptions>(SolarEstimateOptions.Section);
        Assert.Equal("operator-weather-key", loaded.ApiKey);
        Assert.Equal(6.5, loaded.Roof1Kwp);
    }

    [SqlServerFact]
    public async Task RelatedSiteSectionsCommitTogetherAndFailurePreservesBothPreviousValues()
    {
        var failure = new RejectSalesSave();
        await using var database = await SqlServerTestDatabase.CreateAsync("SolarSettingsAtomic",
            configureOptions: options => options.AddInterceptors(failure), seed: TestInstallation.EnsureAsync);
        var factory = database.Factory;
        await using var db = factory.CreateDbContext();
        var settings = new AppSettingsService(factory, new ConfigurationBuilder().Build());
        await settings.SaveSectionsAsync(new Dictionary<string, object>
        {
            ["SolarEstimate"] = new { Roof1Kwp = 5.0 },
            ["SolarSales"] = new { PayNegativePrices = true }
        });
        failure.Armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.SaveSectionsAsync(new Dictionary<string, object>
        {
            ["SolarEstimate"] = new { Roof1Kwp = 10.0 },
            ["SolarSales"] = new { PayNegativePrices = false }
        }));
        var before = await db.AppSettings.AsNoTracking().ToDictionaryAsync(row => row.Section + ":" + row.Key, row => row.Value);
        Assert.Equal("5", before["SolarEstimate:Roof1Kwp"]);
        Assert.Equal("True", before["SolarSales:PayNegativePrices"]);
        failure.Armed = false;
        await settings.SaveSectionsAsync(new Dictionary<string, object>
        {
            ["SolarEstimate"] = new { Roof1Kwp = 10.0 },
            ["SolarSales"] = new { PayNegativePrices = false }
        });
        var after = await db.AppSettings.AsNoTracking().ToDictionaryAsync(row => row.Section + ":" + row.Key, row => row.Value);
        Assert.Equal("10", after["SolarEstimate:Roof1Kwp"]);
        Assert.Equal("False", after["SolarSales:PayNegativePrices"]);
    }
    private sealed class RejectSalesSave : SaveChangesInterceptor
    {
        public bool Armed;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context!.ChangeTracker.Entries<AppSetting>()
                .Any(entry => entry.Entity.Section == "SolarSales" && entry.State is EntityState.Added or EntityState.Modified))
                throw new InvalidOperationException("Injected second-section write failure.");
            return ValueTask.FromResult(result);
        }
    }
}
