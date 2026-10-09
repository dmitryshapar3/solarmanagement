using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Services;
using DeyeSolar.Web.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using DeyeSolar.Web.Integrations;
using Microsoft.AspNetCore.DataProtection;
using SolarManagement.Inverters.Contracts;
using SolarManagement.Integrations.Contracts;

namespace DeyeSolar.Web.Tests;

public class TenantRuntimeSqliteTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ActualTenantContainersSeedAndReloadOnlyTheirOwnSqlSettingsAndCache()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        connection.CreateCollation("Latin1_General_100_BIN2", string.CompareOrdinal);
        var options = new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlite(connection).Options;
        await using (var system = new DeyeSolarDbContext(options))
        {
            await system.Database.EnsureCreatedAsync();
            system.Installations.AddRange(new Installation { Id = "first", CreatedAt = Now }, new Installation { Id = "second", CreatedAt = Now });
            await system.SaveChangesAsync();
        }
        var secrets = new IntegrationSecretStore(new EphemeralDataProtectionProvider());
        using var factory = new TenantRuntimeFactory(options, NullLoggerFactory.Instance, new Clock(), new Lifetime(),
            new TenantTestExecutor(), secrets, new(NullLogger<IntegrationChangeNotifier>.Instance), "operator-weather-key");
        await using var first = await factory.CreateAsync("first");
        await using var second = await factory.CreateAsync("second");
        var firstSettings = first.Resolve<AppSettingsService>();
        var secondSettings = second.Resolve<AppSettingsService>();
        Assert.Equal("", (await firstSettings.LoadSectionAsync<SolarEstimateOptions>("SolarEstimate")).LocationLabel);
        Assert.Equal(0, (await secondSettings.LoadSectionAsync<SolarEstimateOptions>("SolarEstimate")).TotalKwp);
        Assert.Equal("operator-weather-key", second.Resolve<IOptionsMonitor<SolarEstimateOptions>>().CurrentValue.ApiKey);
        Assert.Null(first.Resolve<IConfiguration>()["DeyeCloud:AppSecret"]);
        Assert.Null(second.Resolve<IConfiguration>()["Shelly:AuthKey"]);

        var instanceId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        await using (var integrationDb = new DeyeSolarDbContext(options, "first"))
        {
            integrationDb.IntegrationInstances.Add(new() { Id = instanceId, ProviderId = "fixture.provider", Name = "First inverter",
                PackageVersion = "1.0.0", PackageDigest = "fixture-digest", DescriptorDigest = "fixture-ui", ConfigurationVersion = 1,
                State = "enabled", CreatedAt = Now, UpdatedAt = Now });
            integrationDb.IntegrationConfigurations.Add(new() { InstanceId = instanceId, Revision = 1, ValuesJson = "{}",
                SecretsCiphertext = secrets.Encrypt("first", instanceId, 1, new Dictionary<string, string> { ["apiKey"] = "first-test-only" }), CreatedAt = Now });
            integrationDb.IntegrationDeviceBindings.Add(new() { Id = deviceId, InstanceId = instanceId, Kind = "inverter", RemoteId = "opaque-first",
                Name = "First inverter", Enabled = true, IsDefault = true });
            await integrationDb.SaveChangesAsync();
        }
        await first.Resolve<InverterSelectionMonitor>().RefreshAsync(default);
        await second.Resolve<InverterSelectionMonitor>().RefreshAsync(default);
        Assert.Equal(deviceId.ToString("D"), first.Resolve<IOptionsMonitor<InverterConnectionOptions>>().CurrentValue.DeviceKey);
        Assert.Equal("", second.Resolve<IOptionsMonitor<InverterConnectionOptions>>().CurrentValue.DeviceKey);
        var session = await first.Resolve<IIntegrationRegistry>().GetRuntimeSessionAsync(instanceId, default);
        Assert.Equal("first-test-only", session.Configuration.Secrets["apiKey"]);
        Assert.Null(await second.Resolve<IIntegrationRegistry>().GetSnapshotAsync(instanceId, default));
        await Assert.ThrowsAsync<IntegrationRequestException>(() => second.Resolve<IIntegrationRegistry>().GetRuntimeSessionAsync(instanceId, default));
        await firstSettings.SaveSectionAsync("SolarEstimate", new SolarSiteSettings(51.25, 19.5, "First site", "UTC", 5, 0, 25, 0, 180, 0));
        await firstSettings.SaveSectionAsync("SolarSales", new SalesSiteSettings("2025-01-02", "UTC", false));
        Assert.Equal(5, (await firstSettings.LoadSectionAsync<SolarEstimateOptions>("SolarEstimate")).TotalKwp);
        Assert.Equal(new DateOnly(2025, 1, 2), (await firstSettings.LoadSectionAsync<SolarSalesOptions>("SolarSales")).ContractStartDate);
        Assert.Equal(new DateOnly(2026, 10, 1), (await secondSettings.LoadSectionAsync<SolarSalesOptions>("SolarSales")).ContractStartDate);
        Assert.Equal("", (await secondSettings.LoadSectionAsync<SolarEstimateOptions>("SolarEstimate")).LocationLabel);
        Assert.Equal("operator-weather-key", (await firstSettings.LoadSectionAsync<SolarEstimateOptions>("SolarEstimate")).ApiKey);

        var observation = new CachedSolarObservation("first-only", new(Now, 800, 400, 20, 1, Now, 0), Now);
        await first.Resolve<ISolarEstimateStore>().SaveAsync(observation, default);
        Assert.Equal("first-only", (await first.Resolve<ISolarEstimateStore>().LoadAsync(default))!.ConfigurationKey);
        Assert.Null(await second.Resolve<ISolarEstimateStore>().LoadAsync(default));
        await first.Resolve<IDeviceLabelStore>().SaveAsync(new() { ["test-label"] = "First label" }, default);
        Assert.Empty(await second.Resolve<IDeviceLabelStore>().LoadAsync(default));

        // Editable SQL settings cannot replace the captured operator weather key.
        await using (var firstDb = new DeyeSolarDbContext(options, "first"))
        {
            firstDb.AppSettings.Add(new() { Section = "SolarEstimate", Key = "ApiKey", Value = "untrusted-sql-key" });
            await firstDb.SaveChangesAsync();
        }
        await first.RefreshSettingsAsync();
        Assert.Equal("operator-weather-key", first.Resolve<IOptionsMonitor<SolarEstimateOptions>>().CurrentValue.ApiKey);
        Assert.Equal("operator-weather-key", (await firstSettings.LoadSectionAsync<SolarEstimateOptions>("SolarEstimate")).ApiKey);
        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.CreateAsync("absent"));
    }

    [Fact]
    public async Task LegacySalesDtoPreservesPricingAcrossActualReloadsAndNullableLimitsCanStillBeCleared()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        connection.CreateCollation("Latin1_General_100_BIN2", string.CompareOrdinal);
        var options = new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlite(connection).Options;
        await using (var system = new DeyeSolarDbContext(options))
        {
            await system.Database.EnsureCreatedAsync();
            system.Installations.AddRange(new Installation { Id = "first", CreatedAt = Now }, new Installation { Id = "second", CreatedAt = Now });
            await system.SaveChangesAsync();
        }
        var secrets = new IntegrationSecretStore(new EphemeralDataProtectionProvider());
        using var factory = new TenantRuntimeFactory(options, NullLoggerFactory.Instance, new Clock(), new Lifetime(),
            new TenantTestExecutor(), secrets, new(NullLogger<IntegrationChangeNotifier>.Instance), "operator-weather-key");
        await using var first = await factory.CreateAsync("first");
        await using var second = await factory.CreateAsync("second");
        var settings = first.Resolve<AppSettingsService>();
        var sales = first.Resolve<IOptionsMonitor<SolarSalesOptions>>();
        var estimate = first.Resolve<IOptionsMonitor<SolarEstimateOptions>>();
        Assert.Equal("pse", sales.CurrentValue.PriceSource);
        Assert.Equal(0m, sales.CurrentValue.ManualPricePlnPerKwh);

        const string feedUrl = "https://prices.example.org/export.csv";
        await settings.SaveSectionAsync(SolarSalesOptions.Section,
            new SalesSiteSettings("2025-01-02", "UTC", false, "manual", 0.123456m, feedUrl));
        // These are the fields sent by older clients. Missing pricing fields must not become empty SQL values.
        await settings.SaveSectionAsync(SolarSalesOptions.Section, new SalesSiteSettings("2025-03-04", "UTC", true));
        await settings.SaveSectionAsync(DisplayOptions.Section, new DisplayOptions { TimeZoneId = "Europe/Warsaw" });
        await first.RefreshSettingsAsync();
        Assert.Equal(new DateOnly(2025, 3, 4), sales.CurrentValue.ContractStartDate);
        Assert.True(sales.CurrentValue.PayNegativePrices);
        Assert.Equal("manual", sales.CurrentValue.PriceSource);
        Assert.Equal(0.123456m, sales.CurrentValue.ManualPricePlnPerKwh);
        Assert.Equal(feedUrl, sales.CurrentValue.PriceFeedUrl);
        var loaded = await settings.LoadSectionAsync<SolarSalesOptions>(SolarSalesOptions.Section);
        Assert.Equal(sales.CurrentValue.ManualPricePlnPerKwh, loaded.ManualPricePlnPerKwh);
        Assert.Equal(feedUrl, loaded.PriceFeedUrl);

        await settings.SaveSectionAsync(SolarEstimateOptions.Section, new { InverterAcLimitKw = (double?)5.125 });
        Assert.Equal(5.125, estimate.CurrentValue.InverterAcLimitKw);
        await settings.SaveSectionAsync(SolarEstimateOptions.Section, new { InverterAcLimitKw = (double?)null });
        Assert.Null(estimate.CurrentValue.InverterAcLimitKw);
        Assert.Null((await settings.LoadSectionAsync<SolarEstimateOptions>(SolarEstimateOptions.Section)).InverterAcLimitKw);
        Assert.Equal(0.123456m, sales.CurrentValue.ManualPricePlnPerKwh);

        await using var firstDb = new DeyeSolarDbContext(options, "first");
        var persisted = await firstDb.AppSettings.AsNoTracking().Where(row => row.Section == SolarSalesOptions.Section)
            .ToDictionaryAsync(row => row.Key, row => row.Value);
        Assert.Equal("manual", persisted[nameof(SolarSalesOptions.PriceSource)]);
        Assert.Equal("0.123456", persisted[nameof(SolarSalesOptions.ManualPricePlnPerKwh)]);
        Assert.Equal(feedUrl, persisted[nameof(SolarSalesOptions.PriceFeedUrl)]);
        Assert.Equal("", (await firstDb.AppSettings.AsNoTracking().SingleAsync(row => row.Section == SolarEstimateOptions.Section
            && row.Key == nameof(SolarEstimateOptions.InverterAcLimitKw))).Value);
        Assert.Equal("pse", second.Resolve<IOptionsMonitor<SolarSalesOptions>>().CurrentValue.PriceSource);
        Assert.Equal(0m, second.Resolve<IOptionsMonitor<SolarSalesOptions>>().CurrentValue.ManualPricePlnPerKwh);
        Assert.Equal("", second.Resolve<IOptionsMonitor<SolarSalesOptions>>().CurrentValue.PriceFeedUrl);
        await using var secondDb = new DeyeSolarDbContext(options, "second");
        Assert.Equal("0", (await secondDb.AppSettings.AsNoTracking().SingleAsync(row => row.Section == SolarSalesOptions.Section
            && row.Key == nameof(SolarSalesOptions.ManualPricePlnPerKwh))).Value);
    }

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
