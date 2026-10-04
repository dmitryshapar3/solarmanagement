using System.Net;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using DeyeSolar.Web.Integrations;
using SolarManagement.Integrations.Contracts;
using SolarManagement.Inverters.Contracts;
using Microsoft.AspNetCore.DataProtection;
using System.Text.Json;
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
        var defaults = TenantRuntimeOptions.Defaults(Now);
        Assert.DoesNotContain(defaults.Keys, key => key.StartsWith("DeyeCloud:") || key.StartsWith("Shelly:"));
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
    public async Task RealRuntimeProvidersHaveSeparateCredentialsTokensSnapshotsCachesStoresAndRuleServices()
    {
        using var builder = Builder();
        await using var first = Runtime(builder, "first");
        await using var second = Runtime(builder, "second");
        var firstRegistry = (FixtureIntegrationRegistry)first.Resolve<IIntegrationRegistry>();
        var secondRegistry = (FixtureIntegrationRegistry)second.Resolve<IIntegrationRegistry>();
        firstRegistry.PrimaryId = Guid.NewGuid();
        secondRegistry.PrimaryId = Guid.NewGuid();
        await first.Resolve<InverterSelectionMonitor>().RefreshAsync(default);
        await second.Resolve<InverterSelectionMonitor>().RefreshAsync(default);
        Assert.Equal(firstRegistry.PrimaryId.ToString(), first.Resolve<IOptionsMonitor<InverterConnectionOptions>>().CurrentValue.DeviceKey);
        Assert.Equal(secondRegistry.PrimaryId.ToString(), second.Resolve<IOptionsMonitor<InverterConnectionOptions>>().CurrentValue.DeviceKey);
        Assert.Same(first.Resolve<IInverterDataSource>(), first.Resolve<IInverterDataSource>());
        Assert.NotSame(first.Resolve<IInverterDataSource>(), second.Resolve<IInverterDataSource>());
        Assert.NotSame(first.Resolve<ISocketController>(), second.Resolve<ISocketController>());
        Assert.NotSame(first.Resolve<IIntegrationRegistry>(), second.Resolve<IIntegrationRegistry>());
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
        secondRegistry.PrimaryId = null;
        await second.RunDueWorkAsync(CancellationToken.None); // An empty registry must never request provider transport.
    }

    [Fact]
    public async Task ConfigurationReloadClearsOnlyTheChangedInstallationsOldDeviceSnapshots()
    {
        using var builder = Builder();
        var config = Configuration();
        var registry = new FixtureIntegrationRegistry { PrimaryId = Guid.NewGuid() };
        await using var first = builder.BuildRuntime(new(DatabaseOptions, "first"), config, services => services.AddSingleton<IIntegrationRegistry>(registry));
        await using var second = Runtime(builder, "second");
        first.Resolve<InverterDataSnapshot>().Update(new() { SolarProduction = 1234 });
        second.Resolve<InverterDataSnapshot>().Update(new() { SolarProduction = 5678 });
        registry.PrimaryId = Guid.NewGuid();
        first.Resolve<IntegrationChangeNotifier>().Publish("first", registry.InstanceId);
        await first.Resolve<InverterSelectionMonitor>().RefreshAsync(default);
        Assert.Null(first.Resolve<InverterDataSnapshot>().Current);
        Assert.Equal(5678, second.Resolve<InverterDataSnapshot>().Current!.SolarProduction);
        Assert.Equal(registry.PrimaryId.ToString(), first.Resolve<IOptionsMonitor<InverterConnectionOptions>>().CurrentValue.DeviceKey);
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
    public async Task InstallationStopClosesAdmissionAndDrainsInitializationEvenWhenCallerCancels()
    {
        using var builder = Builder();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TenantRuntime? runtime = null;
        await using var registry = new TenantRuntimeRegistry(async (id, ct) =>
        {
            started.SetResult();
            await release.Task.WaitAsync(ct);
            return runtime = Runtime(builder, id);
        }, _ => Task.FromResult<IReadOnlyList<string>>(["first"]), default);
        var initializing = registry.GetAsync("first");
        await started.Task;
        using var cancelled = new CancellationTokenSource();
        var stopped = registry.StopInstallationAsync("first", cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopped);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.GetAsync("first"));
        release.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => initializing);
        await registry.StopInstallationAsync("first");
        Assert.Throws<ObjectDisposedException>(() => runtime!.Resolve<IInverterDataSource>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.GetAsync("first"));
    }

    [Fact]
    public async Task PausedInstallationCanBeReadmittedOnlyAfterDrainAndNeverAfterRetirement()
    {
        using var builder = Builder();
        var calls = 0;
        await using var registry = new TenantRuntimeRegistry((id, _) =>
        {
            calls++;
            return Task.FromResult(Runtime(builder, id));
        }, _ => Task.FromResult<IReadOnlyList<string>>(["first"]), default);
        var first = await registry.GetAsync("first");
        await registry.PauseInstallationAsync("first");
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.GetAsync("first"));
        registry.ResumeInstallation("first");
        var second = await registry.GetAsync("first");
        Assert.NotSame(first, second);
        Assert.Equal(2, calls);
        await registry.StopInstallationAsync("first");
        Assert.Throws<InvalidOperationException>(() => registry.ResumeInstallation("first"));
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
        Assert.Throws<ObjectDisposedException>(() => runtime.Resolve<IInverterDataSource>());
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
        var configuration = Configuration();
        var integrationRegistry = new FixtureIntegrationRegistry { PrimaryId = Guid.NewGuid() };
        var inverterSource = new UnavailableInverterSource();
        var weather = new EmptyHistoryWeather();
        await using var runtime = builder.BuildRuntime(new(DatabaseOptions, "first"), configuration, services =>
        {
            services.AddSingleton<IInverterDataSource>(inverterSource);
            services.AddSingleton<IIntegrationRegistry>(integrationRegistry);
            services.AddSingleton<ISolarHistoryRadiationSource>(weather);
        });
        await runtime.Resolve<InverterSelectionMonitor>().RefreshAsync(default);
        await using var registry = new TenantRuntimeRegistry((_, _) => Task.FromResult(runtime),
            _ => Task.FromResult<IReadOnlyList<string>>(["first"]), CancellationToken.None);
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton(DatabaseOptions);
        collection.AddSingleton<IHostApplicationLifetime>(new Lifetime());
        collection.AddScoped<CurrentInstallation>();
        collection.AddTenantRequestServices(null);
        collection.AddSingleton(registry);
        await using var host = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using (var request = host.CreateScope())
        {
            request.ServiceProvider.GetRequiredService<CurrentInstallation>().BindOnce("first");
            Assert.Equal(integrationRegistry.PrimaryId.ToString(), request.ServiceProvider.GetRequiredService<IOptionsMonitor<InverterConnectionOptions>>().CurrentValue.DeviceKey);
            _ = request.ServiceProvider.GetRequiredService<IInverterRefreshService>();
            var history = await request.ServiceProvider.GetRequiredService<ISolarHistoryService>().ReadAsync(SolarHistoryPeriod.Today, default);
            Assert.All(history.Points, point => Assert.Null(point.Possible));
            _ = request.ServiceProvider.GetRequiredService<SolarEstimateService>();
        }
        var optionsChanged = false;
        using var subscription = runtime.Resolve<IOptionsMonitor<InverterConnectionOptions>>().OnChange((_, _) => optionsChanged = true);
        integrationRegistry.PrimaryId = Guid.NewGuid();
        await runtime.Resolve<InverterSelectionMonitor>().RefreshAsync(default);
        configuration["SolarEstimate:Roof1Kwp"] = "5";
        configuration.Reload();
        using (var request = host.CreateScope())
        {
            request.ServiceProvider.GetRequiredService<CurrentInstallation>().BindOnce("first");
            Assert.True(optionsChanged);
            Assert.Equal(integrationRegistry.PrimaryId.ToString(), request.ServiceProvider.GetRequiredService<IOptionsMonitor<InverterConnectionOptions>>().CurrentValue.DeviceKey);
            var history = await request.ServiceProvider.GetRequiredService<ISolarHistoryService>().ReadAsync(SolarHistoryPeriod.Today, default);
            Assert.NotEmpty(history.Points);
            Assert.Equal(1, weather.Calls); // Exercises the still-live history semaphore without HTTP/database calls.
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => request.ServiceProvider.GetRequiredService<IInverterRefreshService>().RefreshAsync(default));
            Assert.Equal("Test source is unavailable without transport.", failure.Message);
            Assert.Equal(1, inverterSource.Calls);
        }
    }

    private static TenantRuntimeFactory Builder() => new(DatabaseOptions, NullLoggerFactory.Instance, new Clock(), new Lifetime(),
        new TenantTestExecutor(), new(new EphemeralDataProtectionProvider()), new(NullLogger<IntegrationChangeNotifier>.Instance));
    private static IConfigurationRoot Configuration(Dictionary<string, string?>? changes = null)
    {
        var values = TenantRuntimeOptions.Defaults(Now);
        if (changes is not null) foreach (var (key, value) in changes) values[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
    private static TenantRuntime Runtime(TenantRuntimeFactory builder, string id, Dictionary<string, string?>? changes = null)
        => builder.BuildRuntime(new(DatabaseOptions, id), Configuration(changes), services => services.AddSingleton<IIntegrationRegistry>(new FixtureIntegrationRegistry()));
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
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
    private sealed class FixtureIntegrationRegistry : IIntegrationRegistry
    {
        public Guid InstanceId { get; } = Guid.NewGuid();
        public Guid? PrimaryId { get; set; }
        public Task<IReadOnlyList<IntegrationDeviceBindingEntity>> ListBindingsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IntegrationDeviceBindingEntity>>([]);
        public Task<IntegrationDeviceBindingEntity?> FindBindingAsync(Guid id, CancellationToken ct) => Task.FromResult<IntegrationDeviceBindingEntity?>(null);
        public Task<IntegrationDeviceBindingEntity?> GetPrimaryInverterAsync(CancellationToken ct) => Task.FromResult(PrimaryId is { } id
            ? new IntegrationDeviceBindingEntity { Id = id, InstanceId = InstanceId, Kind = "inverter", Enabled = true } : null);
        public Task<IntegrationRegistrySnapshot?> GetSnapshotAsync(Guid id, CancellationToken ct) => Task.FromResult<IntegrationRegistrySnapshot?>(
            new(new(InstanceId, "fixture", "Fixture inverter", "enabled", 1, 1, "1.0.0", "fixture-digest", "fixture-ui"), IntegrationJson.Element(new { }), "fixture-account"));
        public Task<IntegrationSession> GetRuntimeSessionAsync(Guid id, CancellationToken ct) => throw new InvalidOperationException("The isolated container fixture has no configured integration.");
    }
}

internal sealed class TenantTestExecutor : IIntegrationRuntimeExecutor
{
    public Task<JsonElement> InvokeAsync(IntegrationSession session, string method, JsonElement parameters, CancellationToken ct)
        => throw new InvalidOperationException("This fixture does not allow external provider operations.");
    public Task StopAsync(Guid instanceId, CancellationToken ct) => Task.CompletedTask;
}
