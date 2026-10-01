using System.Net;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using DeyeSolar.Infrastructure.DeyeCloud;
using DeyeSolar.Infrastructure.Shelly;
using DeyeSolar.RuleEngine;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Services;
using DeyeSolar.Web.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace DeyeSolar.Web.Tests;

public class TenantRuntimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
    private static DbContextOptions<DeyeSolarDbContext> DatabaseOptions => new DbContextOptionsBuilder<DeyeSolarDbContext>()
        .UseSqlServer("Server=localhost;Database=NeverConnected;User Id=unused;Password=unused;Encrypt=False").Options;

    [Fact]
    public void FreshInstallationNeverInheritsDeploymentCredentialsLocationCapacityOrContract()
    {
        var deployment = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DeyeCloud:AppSecret"] = "legacy-test-secret", ["DeyeCloud:DeviceSn"] = "legacy-device",
            ["Shelly:AuthKey"] = "legacy-shelly-key", ["Shelly:ServerUri"] = "https://legacy.shelly.cloud",
            ["SolarEstimate:Latitude"] = "50", ["SolarEstimate:LocationLabel"] = "Legacy private site",
            ["SolarEstimate:Roof1Kwp"] = "4", ["SolarSales:ContractStartDate"] = "2026-09-28"
        }).Build();
        var defaults = TenantRuntimeOptions.ForInstallation("new-installation", Now, deployment, "legacy-weather-key");
        Assert.Equal("", defaults["DeyeCloud:AppSecret"]);
        Assert.Equal("", defaults["DeyeCloud:DeviceSn"]);
        Assert.Equal("", defaults["Shelly:AuthKey"]);
        Assert.Equal("", defaults["Shelly:ServerUri"]);
        Assert.Equal("0", defaults["SolarEstimate:Latitude"]);
        Assert.Equal("0", defaults["SolarEstimate:Roof1Kwp"]);
        Assert.Equal("0", defaults["SolarEstimate:Roof2Kwp"]);
        Assert.Equal("", defaults["SolarEstimate:LocationLabel"]);
        Assert.Equal("", defaults["SolarEstimate:ApiKey"]);
        Assert.Equal("UTC", defaults["Display:TimeZoneId"]);
        Assert.Equal("2026-10-01", defaults["SolarSales:ContractStartDate"]);
        var solar = new SolarEstimateOptions();
        new ConfigurationBuilder().AddInMemoryCollection(defaults).Build().GetSection("SolarEstimate").Bind(solar);
        Assert.False(TenantRuntimeOptions.HasSolarConfiguration(solar));
    }

    [Fact]
    public void LegacyInstallationKeepsItsOriginalTypedSiteDefaultsAndOnlyItsCapturedServerWeatherKey()
    {
        var deployment = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["DeyeCloud:DeviceSn"] = "existing-device", ["SolarEstimate:ApiKey"] = "untrusted-sql-key" }).Build();
        var values = TenantRuntimeOptions.ForInstallation(InstallationIds.Legacy, Now, deployment, "captured-server-key");
        var options = new SolarEstimateOptions();
        Assert.Equal(AppSettingsService.ToSettingValue(options.Latitude), values["SolarEstimate:Latitude"]);
        Assert.Equal(AppSettingsService.ToSettingValue(options.Roof1Kwp), values["SolarEstimate:Roof1Kwp"]);
        Assert.Equal(options.LocationLabel, values["SolarEstimate:LocationLabel"]);
        Assert.Equal("existing-device", values["DeyeCloud:DeviceSn"]);
        Assert.Equal("captured-server-key", values["SolarEstimate:ApiKey"]);
        Assert.False(TenantRuntimeOptions.KnownSetting("SolarEstimate", "ApiKey"));
    }

    [Fact]
    public async Task RealRuntimeProvidersHaveSeparateCredentialsTokensSnapshotsCachesStoresAndRuleServices()
    {
        using var builder = Builder();
        await using var first = Runtime(builder, "first", new() { ["DeyeCloud:DeviceSn"] = "inverter-a", ["DeyeCloud:AppSecret"] = "test-a" });
        await using var second = Runtime(builder, "second", new() { ["DeyeCloud:DeviceSn"] = "inverter-b", ["DeyeCloud:AppSecret"] = "test-b" });
        Assert.Equal("inverter-a", first.Resolve<IOptionsMonitor<DeyeCloudOptions>>().CurrentValue.DeviceSn);
        Assert.Equal("inverter-b", second.Resolve<IOptionsMonitor<DeyeCloudOptions>>().CurrentValue.DeviceSn);
        Assert.Same(first.Resolve<DeyeCloudClient>(), first.Resolve<DeyeCloudClient>());
        Assert.NotSame(first.Resolve<DeyeCloudClient>(), second.Resolve<DeyeCloudClient>());
        Assert.NotSame(first.Resolve<ShellyCloudClient>(), second.Resolve<ShellyCloudClient>());
        Assert.NotSame(first.Resolve<RuleEvaluator>(), second.Resolve<RuleEvaluator>());
        Assert.NotSame(first.Resolve<SolarEstimateService>(), second.Resolve<SolarEstimateService>());
        Assert.Null(second.Resolve<SolarEstimateService>().Current.Estimate);
        Assert.Contains("Configure your solar installation", second.Resolve<SolarEstimateService>().Current.Error);
        Assert.NotSame(first.Resolve<ISolarHistoryService>(), second.Resolve<ISolarHistoryService>());
        Assert.NotSame(first.Resolve<IExportSalesService>(), second.Resolve<IExportSalesService>());
        Assert.NotSame(first.Resolve<IRuleRepository>(), second.Resolve<IRuleRepository>());
        Assert.NotSame(first.Resolve<IIntegrationTestService>(), second.Resolve<IIntegrationTestService>());
        first.Resolve<InverterDataSnapshot>().Update(new() { SolarProduction = 1234 });
        first.Resolve<DeviceStatusSnapshot>().Update([new("a", "First socket", "Shelly", true, false, 10)]);
        Assert.Null(second.Resolve<InverterDataSnapshot>().Current);
        Assert.Null(second.Resolve<DeviceStatusSnapshot>().Current);
        using var firstContext = first.Resolve<IDbContextFactory<DeyeSolarDbContext>>().CreateDbContext();
        using var secondContext = second.Resolve<IDbContextFactory<DeyeSolarDbContext>>().CreateDbContext();
        Assert.Equal("first", firstContext.InstallationId);
        Assert.Equal("second", secondContext.InstallationId);
        await second.RunDueWorkAsync(CancellationToken.None); // Unconfigured installations must perform no network/database work.
    }

    [Fact]
    public async Task ConfigurationReloadClearsOnlyTheChangedInstallationsOldDeviceSnapshots()
    {
        using var builder = Builder();
        var config = Configuration(new() { ["DeyeCloud:DeviceSn"] = "old-device" });
        await using var first = builder.BuildRuntime(new(DatabaseOptions, "first"), config);
        await using var second = Runtime(builder, "second");
        first.Resolve<InverterDataSnapshot>().Update(new() { SolarProduction = 1234 });
        second.Resolve<InverterDataSnapshot>().Update(new() { SolarProduction = 5678 });
        config["DeyeCloud:DeviceSn"] = "new-device";
        config.Reload();
        Assert.Null(first.Resolve<InverterDataSnapshot>().Current);
        Assert.Equal(5678, second.Resolve<InverterDataSnapshot>().Current!.SolarProduction);
        Assert.Equal("new-device", first.Resolve<IOptionsMonitor<DeyeCloudOptions>>().CurrentValue.DeviceSn);
    }

    [Fact]
    public async Task SingleFlightInitializationAndCallerCancellationCannotReplaceAnotherTenantsRuntime()
    {
        using var builder = Builder();
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var registry = new TenantRuntimeRegistry(async (id, ct) =>
        {
            Interlocked.Increment(ref calls);
            await start.Task.WaitAsync(ct);
            return Runtime(builder, id);
        }, _ => Task.FromResult<IReadOnlyList<string>>(["first", "second"]), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var abandoned = registry.GetAsync("first", cancellation.Token);
        var first = registry.GetAsync("first");
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        start.SetResult(true);
        var firstRuntime = await first;
        Assert.Same(firstRuntime, await registry.GetAsync("first"));
        var secondRuntime = await registry.GetAsync("second");
        Assert.NotSame(firstRuntime, secondRuntime);
        Assert.Equal(2, calls);
        Assert.Throws<ArgumentException>(() => registry.Get(""));
    }

    [Fact]
    public async Task RuntimeShutdownClearsPrivateSnapshotsAndRejectsFurtherResolution()
    {
        using var builder = Builder();
        var runtime = Runtime(builder, "first");
        var inverter = runtime.Resolve<InverterDataSnapshot>();
        var devices = runtime.Resolve<DeviceStatusSnapshot>();
        inverter.Update(new() { SolarProduction = 1234 });
        devices.Update([new("a", "First socket", "Shelly", true, false, 10)]);
        await runtime.DisposeAsync();
        await runtime.DisposeAsync();
        Assert.Null(inverter.Current);
        Assert.Null(devices.Current);
        Assert.Throws<ObjectDisposedException>(() => runtime.Resolve<DeyeCloudClient>());
    }

    [Fact]
    public async Task RuntimeStopCancelsOutstandingCloudTransportAndReleasesTheGlobalRequestSlot()
    {
        using var gate = new SemaphoreSlim(1, 1);
        using var stopping = new CancellationTokenSource();
        var transport = new WaitingTransport();
        using var client = new HttpClient(new TenantRequestGate(gate, stopping.Token) { InnerHandler = transport });
        var request = client.GetAsync("https://official.example/no-network");
        await transport.Started.Task;
        stopping.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task DisposingAHostRequestScopeCannotDisposeTheInstallationsRefreshHistoryOrOptions()
    {
        using var builder = Builder();
        var configuration = Configuration(new() { ["DeyeCloud:DeviceSn"] = "old-device" });
        var inverterSource = new UnavailableInverterSource();
        var weather = new EmptyHistoryWeather();
        await using var runtime = builder.BuildRuntime(new(DatabaseOptions, "first"), configuration, services =>
        {
            services.AddSingleton<IInverterDataSource>(inverterSource);
            services.AddSingleton<ISolarHistoryRadiationSource>(weather);
        });
        await using var registry = new TenantRuntimeRegistry((_, _) => Task.FromResult(runtime),
            _ => Task.FromResult<IReadOnlyList<string>>(["first"]), CancellationToken.None);
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton(DatabaseOptions);
        collection.AddSingleton<IHostApplicationLifetime>(new Lifetime());
        collection.AddScoped<CurrentInstallation>();
        collection.AddTenantRequestServices(new ConfigurationBuilder().Build(), null);
        collection.AddSingleton(registry);
        await using var host = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using (var request = host.CreateScope())
        {
            request.ServiceProvider.GetRequiredService<CurrentInstallation>().BindOnce("first");
            Assert.Equal("old-device", request.ServiceProvider.GetRequiredService<IOptionsMonitor<DeyeCloudOptions>>().CurrentValue.DeviceSn);
            _ = request.ServiceProvider.GetRequiredService<IInverterRefreshService>();
            var history = await request.ServiceProvider.GetRequiredService<ISolarHistoryService>().ReadAsync(SolarHistoryPeriod.Today, default);
            Assert.All(history.Points, point => Assert.Null(point.Possible));
            _ = request.ServiceProvider.GetRequiredService<SolarEstimateService>();
        }
        var optionsChanged = false;
        using var subscription = runtime.Resolve<IOptionsMonitor<DeyeCloudOptions>>().OnChange((_, _) => optionsChanged = true);
        configuration["DeyeCloud:DeviceSn"] = "new-device";
        configuration["SolarEstimate:Roof1Kwp"] = "5";
        configuration.Reload();
        using (var request = host.CreateScope())
        {
            request.ServiceProvider.GetRequiredService<CurrentInstallation>().BindOnce("first");
            Assert.True(optionsChanged);
            Assert.Equal("new-device", request.ServiceProvider.GetRequiredService<IOptionsMonitor<DeyeCloudOptions>>().CurrentValue.DeviceSn);
            var history = await request.ServiceProvider.GetRequiredService<ISolarHistoryService>().ReadAsync(SolarHistoryPeriod.Today, default);
            Assert.NotEmpty(history.Points);
            Assert.Equal(1, weather.Calls); // Exercises the still-live history semaphore without HTTP/database calls.
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => request.ServiceProvider.GetRequiredService<IInverterRefreshService>().RefreshAsync(default));
            Assert.Equal("Test source is unavailable without transport.", failure.Message);
            Assert.Equal(1, inverterSource.Calls);
        }
    }

    [Theory]
    [InlineData("deye", "http://eu1-developer.deyecloud.com/v1.0/account/token?appId=test", false)]
    [InlineData("deye", "https://foreign.example/v1.0/account/token?appId=test", false)]
    [InlineData("deye", "https://eu1-developer.deyecloud.com/other", false)]
    [InlineData("deye", "https://eu1-developer.deyecloud.com/v1.0/account/token?appId=test", true)]
    [InlineData("shelly", "https://foreign.example/device/all_status?auth_key=test", false)]
    [InlineData("shelly", "http://shelly-1-eu.shelly.cloud/device/all_status?auth_key=test", false)]
    [InlineData("shelly", "https://shelly-1-eu.shelly.cloud/device/all_status?auth_key=test", true)]
    public async Task ActualCloudRequestsRequireOfficialHttpsOriginsBeforeAnyTransport(string provider, string url, bool allowed)
    {
        var transport = new CountingTransport();
        using var client = new HttpClient(new TenantProviderEndpointGuard(provider) { InnerHandler = transport });
        if (allowed) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode);
        else await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url));
        Assert.Equal(allowed ? 1 : 0, transport.Calls);
    }

    private static TenantRuntimeFactory Builder() => new(DatabaseOptions, new ConfigurationBuilder().Build(), NullLoggerFactory.Instance, new Clock(), new Lifetime());
    private static IConfigurationRoot Configuration(Dictionary<string, string?>? changes = null)
    {
        var values = TenantRuntimeOptions.Defaults(Now);
        if (changes is not null) foreach (var (key, value) in changes) values[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
    private static TenantRuntime Runtime(TenantRuntimeFactory builder, string id, Dictionary<string, string?>? changes = null)
        => builder.BuildRuntime(new(DatabaseOptions, id), Configuration(changes));
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
    private sealed class CountingTransport : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }
    }
    private sealed class WaitingTransport : HttpMessageHandler
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Started.SetResult(true); await Task.Delay(Timeout.Infinite, ct); return new(HttpStatusCode.OK); }
    }
    private sealed class UnavailableInverterSource : IInverterDataSource
    {
        public int Calls { get; private set; }
        public Task<InverterData> ReadCurrentDataAsync(CancellationToken ct)
        { Calls++; return Task.FromException<InverterData>(new InvalidOperationException("Test source is unavailable without transport.")); }
    }
    private sealed class EmptyHistoryWeather : ISolarHistoryRadiationSource
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<SolarWeatherSample>> ReadAsync(SolarEstimateOptions options, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
        { Calls++; return Task.FromResult<IReadOnlyList<SolarWeatherSample>>([]); }
    }
}
