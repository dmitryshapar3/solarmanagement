using DeyeSolar.Web.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;

namespace DeyeSolar.Web.Tests;

public sealed class SettingsTransactionTests
{
    [SqlServerFact]
    public async Task RelatedSiteSectionsCommitTogetherAndFailurePreservesBothPreviousValues()
    {
        var failure = new RejectSalesSave();
        var connection = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SOLAR_TEST_SQL_CONNECTION"))
        { InitialCatalog = "SolarSettingsAtomic_" + Guid.NewGuid().ToString("N") };
        var options = new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlServer(connection.ConnectionString)
            .AddInterceptors(failure).Options;
        var factory = new Factory(options);
        await using var db = factory.CreateDbContext();
        try
        {
            await db.Database.MigrateAsync();
                await TestInstallation.EnsureAsync(db);
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
        finally { await db.Database.EnsureDeletedAsync(); }
    }
    private sealed class Factory(DbContextOptions<DeyeSolarDbContext> options) : IDbContextFactory<DeyeSolarDbContext>
    {
        public DeyeSolarDbContext CreateDbContext() => new(options, TestInstallation.Id);
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
