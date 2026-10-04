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
        var deployment = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["DeyeCloud:AppSecret"] = "legacy-test-only", ["SolarEstimate:LocationLabel"] = "Legacy site" }).Build();
        var secrets = new IntegrationSecretStore(new EphemeralDataProtectionProvider());
        using var factory = new TenantRuntimeFactory(options, deployment, NullLoggerFactory.Instance, new Clock(), new Lifetime(),
            new TenantTestExecutor(), secrets, new(NullLogger<IntegrationChangeNotifier>.Instance), "legacy-key");
        await using var first = await factory.CreateAsync("first");
        await using var second = await factory.CreateAsync("second");
        var firstSettings = first.Resolve<AppSettingsService>();
        var secondSettings = second.Resolve<AppSettingsService>();
        Assert.Equal("", (await firstSettings.LoadSectionAsync<SolarEstimateOptions>("SolarEstimate")).LocationLabel);
        Assert.Equal(0, (await secondSettings.LoadSectionAsync<SolarEstimateOptions>("SolarEstimate")).TotalKwp);
        Assert.Equal("", (await secondSettings.LoadSectionAsync<DeyeCloudOptions>("DeyeCloud")).AppSecret);

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
        Assert.Equal("", (await firstSettings.LoadSectionAsync<SolarEstimateOptions>("SolarEstimate")).ApiKey);

        var observation = new CachedSolarObservation("first-only", new(Now, 800, 400, 20, 1, Now, 0), Now);
        await first.Resolve<ISolarEstimateStore>().SaveAsync(observation, default);
        Assert.Equal("first-only", (await first.Resolve<ISolarEstimateStore>().LoadAsync(default))!.ConfigurationKey);
        Assert.Null(await second.Resolve<ISolarEstimateStore>().LoadAsync(default));
        await first.Resolve<IDeviceLabelStore>().SaveAsync(new() { ["test-label"] = "First label" }, default);
        Assert.Empty(await second.Resolve<IDeviceLabelStore>().LoadAsync(default));

        // Neither a persisted key nor deployment fallback may select a server weather key for a new account.
        await using (var firstDb = new DeyeSolarDbContext(options, "first"))
        {
            firstDb.AppSettings.Add(new() { Section = "SolarEstimate", Key = "ApiKey", Value = "untrusted-sql-key" });
            await firstDb.SaveChangesAsync();
        }
        await first.RefreshSettingsAsync();
        Assert.Equal("", first.Resolve<IOptionsMonitor<SolarEstimateOptions>>().CurrentValue.ApiKey);
        Assert.Equal("", (await firstSettings.LoadSectionAsync<SolarEstimateOptions>("SolarEstimate")).ApiKey);
        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.CreateAsync("absent"));
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
