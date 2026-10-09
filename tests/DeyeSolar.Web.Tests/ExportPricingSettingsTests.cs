using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Web.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SolarManagement.Inverters.Contracts;
using DeyeSolar.Web.Services;

namespace DeyeSolar.Web.Tests;

public sealed class ExportPricingSettingsTests
{
    [Fact]
    public void OlderClientsPreserveSavedPricingAndNewClientsCanExplicitlyReturnToPse()
    {
        var current = new SalesSiteSettings("2026-09-28", "Europe/Warsaw", false, "feed", 0.345678m, "https://example.com/prices.xml");
        var older = new SalesSiteSettings("2026-10-01", "Europe/Warsaw", true).PreservePricing(current);
        Assert.Equal("feed", older.PriceSource); Assert.Equal(current.ManualPricePlnPerKwh, older.ManualPricePlnPerKwh); Assert.Equal(current.PriceFeedUrl, older.PriceFeedUrl);
        Assert.Equal("2026-10-01", older.ContractStartDate); Assert.True(older.PayNegativePrices);
        Assert.Equal("pse", (older with { PriceSource = "pse" }).PreservePricing(current).PriceSource);
    }
    [Theory]
    [InlineData("manual", -0.1)][InlineData("manual", 1000.1)][InlineData("manual", 0.0000001)][InlineData("unknown", 0)]
    public void UnsupportedSourcesAndManualPricesAreRejected(string source, double amount)
        => Assert.Throws<ArgumentException>(() => new SolarSalesOptions { PriceSource = source, ManualPricePlnPerKwh = (decimal)amount }.Validate());
    [Fact]
    public void ExactZeroAndSixDecimalManualPricesAreValid()
    {
        new SolarSalesOptions { PriceSource = "manual", ManualPricePlnPerKwh = 0 }.Validate();
        new SolarSalesOptions { PriceSource = "manual", ManualPricePlnPerKwh = 0.123456m }.Validate();
    }
    [Fact]
    public async Task OlderV1SiteSaveCannotOverwritePricingChangedAfterItsValidationRead()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        connection.CreateCollation("Latin1_General_100_BIN2", string.CompareOrdinal);
        var factory = new Factory(new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlite(connection).Options);
        await using (var db = factory.CreateDbContext()) { await db.Database.EnsureCreatedAsync(); db.Installations.Add(new() { Id = "pricing", CreatedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(); }
        var settings = new AppSettingsService(factory, new ConfigurationBuilder().Build());
        await settings.SaveSectionAsync(SolarSalesOptions.Section, new SolarSalesOptions { PriceSource = "manual", ManualPricePlnPerKwh = 0.3m });
        var reader = new ConcurrentReader(settings);
        var site = new SiteSettingsDto(new(0, 0, "Site", "UTC", 5, 0, 20, 0, 180, 0), new("2026-10-01", "UTC", false));
        await new SiteSettingsService(reader, settings, new FixedOptionsMonitor<InverterConnectionOptions>(new()), new NoInverter()).SaveAsync(site);
        var result = await settings.LoadSectionAsync<SolarSalesOptions>(SolarSalesOptions.Section);
        Assert.Equal("feed", result.PriceSource); Assert.Equal(0.9m, result.ManualPricePlnPerKwh); Assert.Equal("https://example.com/new.xml", result.PriceFeedUrl);
        Assert.Equal(new DateOnly(2026, 10, 1), result.ContractStartDate); Assert.Equal("UTC", result.TimeZoneId);
    }
    private sealed class ConcurrentReader(AppSettingsService inner) : IAppSettingsReader
    {
        public async Task<T> LoadSectionAsync<T>(string section) where T : new()
        {
            var captured = await inner.LoadSectionAsync<T>(section);
            if (section == SolarSalesOptions.Section) await inner.SaveSectionAsync(section,
                new { PriceSource = "feed", ManualPricePlnPerKwh = 0.9m, PriceFeedUrl = "https://example.com/new.xml" });
            return captured;
        }
    }
    private sealed class Factory(DbContextOptions<DeyeSolarDbContext> options) : IDbContextFactory<DeyeSolarDbContext>
    { public DeyeSolarDbContext CreateDbContext() => new(options, "pricing"); }
    private sealed class NoInverter : IInverterDataSource
    { public Task<InverterData> ReadCurrentDataAsync(CancellationToken ct) => throw new InvalidOperationException(); }

}
