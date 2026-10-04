using SolarManagement.Inverters.Contracts;
using SolarManagement.SmartSockets.Contracts;
using Watts = SolarManagement.Inverters.Contracts.Watts;
using System.Net;
using System.Text.RegularExpressions;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Services;
using DeyeSolar.Web.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;
using DashboardPage = DeyeSolar.Web.Pages.Index;

namespace DeyeSolar.Web.Tests;

public class DashboardTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);
    private static InverterData Reading(int soc = 87, int battery = -2400, int grid = -1200, int solar = 4100) => ConfirmedInverterReading.Create(new()
    {
        BatterySoc = soc, BatteryPower = battery, GridConsumption = grid, SolarProduction = solar, Timestamp = Timestamp,
        SolarObservedAt = Timestamp.AddMinutes(-1), SolarDeviceSn = "test-device",
        GridObservedAt = Timestamp.AddMinutes(-1), GridDeviceSn = "test-device",
        BatteryVoltage = 51.5, BatteryCurrent = 4.2, BatteryTemperature = 24, LoadPower = 900
    });

    [Fact]
    public async Task GenerationDetailKeepsBothPowerValuesAndTheFullHistoryControls()
    {
        var services = ComponentServices();
        services.AddSingleton<InverterDataSnapshot>();
        services.AddOptions<SolarEstimateOptions>();
        services.Configure<InverterConnectionOptions>(options => options.DeviceKey = "test-device");
        services.AddSingleton<ISolarRadiationSource, UnusedRadiationSource>();
        services.AddSingleton<ISolarEstimateStore, UnusedEstimateStore>();
        services.AddSingleton<SolarEstimateService>();
        services.AddSingleton<ISolarHistoryService, HistoryService>();
        services.AddSingleton<IInverterRefreshService>(new RefreshService(new InverterDataSnapshot()));
        await using var provider = services.BuildServiceProvider();
        var html = await RenderAsync<DeyeSolar.Web.Pages.Generation>(provider);

        Assert.Contains("Solar generation", html);
        Assert.Contains("Latest solar snapshot", html);
        Assert.Single(Regex.Matches(html, "data-testid=\"solar-possible\""));
        Assert.Single(Regex.Matches(html, "data-testid=\"solar-actual\""));
        Assert.Contains("Possible", html);
        Assert.Contains("Latest inverter generation", html);
        Assert.Contains("aria-label=\"Chart period\"", html);
        Assert.Contains("aria-label=\"Day navigation\"", html);
        Assert.Contains("aria-label=\"Previous day\"", html);
        Assert.Contains("aria-label=\"Next day\"", html);
        foreach (var period in new[] { "Day", "7 days", "30 days" }) Assert.Matches($">{period}</button>", html);
        Assert.DoesNotContain("history-overview-paper", html);
        Assert.DoesNotMatch("[\\u0400-\\u04ff]", html);
    }

    [Fact]
    public async Task NavigationKeepsDedicatedGenerationAndSalesLinksWithEnglishLabels()
    {
        await using var services = ComponentServices().BuildServiceProvider();
        var html = await RenderAsync<NavMenu>(services);

        Assert.Matches("<a[^>]*href=\"/generation\"[^>]*>[\\s\\S]*?Generation[\\s\\S]*?</a>", html);
        Assert.Matches("<a[^>]*href=\"/sales\"[^>]*>[\\s\\S]*?Sales[\\s\\S]*?</a>", html);
        foreach (var route in new[] { "/", "/devices", "/rules", "/runs", "/history", "/settings" })
            Assert.Contains($"href=\"{route}\"", html);
        Assert.DoesNotMatch("[\\u0400-\\u04ff]", html);
    }

    [Fact]
    public void GenerationAndSalesDetailRoutesRetainTheInheritedAuthorizationRequirement()
    {
        foreach (var (page, route) in new[] { (typeof(DeyeSolar.Web.Pages.Generation), "/generation"), (typeof(DeyeSolar.Web.Pages.Sales), "/sales") })
        {
            Assert.Equal(route, Assert.Single(page.GetCustomAttributes(typeof(RouteAttribute), true).Cast<RouteAttribute>()).Template);
            Assert.NotEmpty(page.GetCustomAttributes(true).OfType<IAuthorizeData>());
            Assert.Empty(page.GetCustomAttributes(true).OfType<IAllowAnonymous>());
        }
    }

    private static ServiceCollection ComponentServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddComponentLocalization();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime, NullJsRuntime>();
        services.AddSingleton<TimeProvider>(new Clock());
        services.AddSingleton<NavigationManager, TestNavigation>();
        return services;
    }

    private static async Task<string> RenderAsync<T>(IServiceProvider services) where T : IComponent
    {
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<T>();
            return WebUtility.HtmlDecode(rendered.ToHtmlString());
        });
    }

    [SqlServerFact]
    public async Task SolarGenerationDoesNotUseBatteryChargingOrDischargingPower()
    {
        foreach (var (solar, battery, expected) in new[] { (4100, -2742, "4.10"), (4100, 2742, "4.10"), (0, 2742, "0.00") })
        {
            await using var fixture = await Fixture.CreateAsync(Reading() with { SolarProduction = solar, BatteryPower = battery });
            var html = await RenderAsync<DashboardHost>(fixture.Services);

            var metric = Regex.Match(html, "data-testid=\"solar-generation\"[^>]*>[\\s\\S]*?</div>").Value;
            Assert.Matches($">{Regex.Escape(expected)}<small[^>]*>kW", metric);
            Assert.Contains("Solar generation", metric);
            Assert.DoesNotContain("2,742", metric);
            Assert.DoesNotContain("Charging", metric);
            Assert.DoesNotContain("Discharging", metric);
            await fixture.AssertNoMutationsAsync();
        }
    }

    [SqlServerFact]
    public async Task DashboardDistinguishesUnavailableGridAndSolarFromConfirmedZeroWithoutMutations()
    {
        foreach (var quality in new[] { MeasurementQuality.Missing, MeasurementQuality.Invalid, MeasurementQuality.Stale, MeasurementQuality.Good })
        {
            var zero = Reading(grid: 0, solar: 0) with { Telemetry = ZeroFlows(quality) };
            await using var fixture = await Fixture.CreateAsync(zero);
            var html = await RenderAsync<DashboardHost>(fixture.Services);
            var grid = Regex.Match(html, "data-testid=\"grid-power\"[^>]*>[\\s\\S]*?</div>").Value;
            var solar = Regex.Match(html, "data-testid=\"solar-generation\"[^>]*>[\\s\\S]*?</div>").Value;
            if (quality == MeasurementQuality.Good)
            {
                Assert.Contains("Idle", grid);
                Assert.Matches(">0<small[^>]*>W", grid);
                Assert.Matches(">0[.]00<small[^>]*>kW", solar);
            }
            else
            {
                Assert.DoesNotContain("Idle", grid);
                Assert.DoesNotContain("energy-status-positive", grid);
                Assert.DoesNotContain("energy-status-active", grid);
                Assert.Matches(">—<small[^>]*>W", grid);
                Assert.Matches(">—<small[^>]*>kW", solar);
                Assert.Contains("Grid measurement unavailable", grid);
            }
            await fixture.AssertNoMutationsAsync();
        }
        await using var unverified = await Fixture.CreateAsync(Reading(grid: 0, solar: 0) with { Telemetry = null, BatterySocValid = false });
        var unverifiedHtml = await RenderAsync<DashboardHost>(unverified.Services);
        var unknownGrid = Regex.Match(unverifiedHtml, "data-testid=\"grid-power\"[^>]*>[\\s\\S]*?</div>").Value;
        Assert.DoesNotContain("Idle", unknownGrid);
        Assert.Matches(">—<small[^>]*>W", unknownGrid);
        await unverified.AssertNoMutationsAsync();
    }

    private static IInverterTelemetry ZeroFlows(MeasurementQuality quality) => new InverterTelemetry(new(Guid.NewGuid()), Timestamp,
        new(new Percent(87), Timestamp, MeasurementQuality.Good), new(new Watts(-2400), Timestamp, MeasurementQuality.Good),
        new(new Celsius(24), Timestamp, MeasurementQuality.Good), new(new Volts(51.5), Timestamp, MeasurementQuality.Good),
        new(new Amperes(4.2), Timestamp, MeasurementQuality.Good), new(new Watts(0), Timestamp, quality), new(new Watts(0), Timestamp, quality),
        new(new Watts(900), Timestamp, MeasurementQuality.Good), SolarManagement.Inverters.Contracts.SolarPowerBasis.PvDc);

    [SqlServerFact]
    public async Task UnifiedStatusPrecedesTheTwoChartsAndPreservesQuickActionsAndRules()
    {
        await using var fixture = await Fixture.CreateAsync(Reading());
        await using var renderer = new HtmlRenderer(fixture.Services, fixture.Services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<DashboardHost>();
            return WebUtility.HtmlDecode(rendered.ToHtmlString());
        });

        Assert.Single(Regex.Matches(html, "data-testid=\"energy-status\""));
        AssertOrdered(html, "data-testid=\"energy-status\"", "data-testid=\"solar-generation\"", "data-testid=\"load-power\"",
            "data-testid=\"grid-power\"", "data-testid=\"battery-power\"", "data-testid=\"dashboard-charts\"", "data-testid=\"dashboard-generation\"", "data-testid=\"dashboard-sales\"");
        Assert.DoesNotContain("data-testid=\"battery-soc\"", html);
        Assert.Matches("data-testid=\"load-power\"[^>]*>[\\s\\S]*?>900<small[^>]*>W", html);
        Assert.Matches("data-testid=\"solar-generation\"[^>]*>[\\s\\S]*?>4[.]10<small[^>]*>kW", html);
        Assert.Matches("data-testid=\"grid-power\"[^>]*>[\\s\\S]*?>1,200<small[^>]*>W", html);
        Assert.Matches("data-testid=\"battery-power\"[^>]*>[\\s\\S]*?Battery charging[\\s\\S]*?>2,400<small[^>]*>W", html);
        Assert.Contains("Exporting", html);
        Assert.Contains("12:00:00", html);
        Assert.Contains("aria-label=\"Refresh inverter data\"", html);
        Assert.Contains("href=\"/solar-details?returnTo=%2F\"", html);
        Assert.Contains("href=\"/sales-details?period=Day&date=2026-09-30&returnTo=%2F\"", html);
        Assert.Contains("href=\"/inverter-details?returnTo=%2F\"", html);
        AssertOrdered(html, "data-testid=\"dashboard-sales\"", "sales-paper", "id=\"sales-title\"");
        Assert.Contains("Quick Actions", html);
        Assert.Contains("Socket ON", html);
        Assert.Contains("Socket OFF", html);
        Assert.Contains("Independent garden socket", html);
        Assert.Contains("Last checked", html);
        Assert.DoesNotContain("Battery Details", html);
        Assert.DoesNotContain("<td>Voltage</td>", html);
        Assert.DoesNotContain("<td>Temperature</td>", html);
        Assert.DoesNotMatch("[\\u0400-\\u04ff]", html);
        Assert.Equal(0, fixture.Refresh.Calls);
        await fixture.AssertNoMutationsAsync();
    }

    [SqlServerFact]
    public async Task RefreshDispatchesTheCanonicalServiceOnlyOnceForDuplicateClicksAndShowsItsNewSnapshot()
    {
        await using var fixture = await Fixture.CreateAsync(Reading());
        fixture.Refresh.Hold = true;
        await using var renderer = fixture.Renderer();
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            var button = renderer.RefreshButton(root);
            var first = renderer.DispatchAsync(button.EventId);
            Assert.True(renderer.RefreshButton(root).Disabled);
            Assert.Contains("Refreshing…", renderer.Text(root));
            Assert.Contains("4.10", renderer.Text(root));
            await renderer.DispatchAsync(button.EventId);
            Assert.Equal(1, fixture.Refresh.Calls);
            fixture.Refresh.Complete(Reading(62, 1000, 750, 5200) with { Timestamp = Timestamp.AddMinutes(5) });
            await first;
            Assert.False(renderer.RefreshButton(root).Disabled);
            Assert.Contains("Battery discharging", renderer.Text(root));
            Assert.Contains("Importing", renderer.Text(root));
            Assert.Contains("5.20", renderer.Text(root));
            Assert.Contains("1,000", renderer.Text(root));
            Assert.DoesNotContain("4.10", renderer.Text(root));
            Assert.Contains("12:05:00", renderer.Text(root));
            Assert.Same(fixture.Refresh.LastResult, fixture.Snapshot.Current);
        });
        await fixture.AssertNoMutationsAsync();
    }

    [SqlServerFact]
    public async Task FailedRefreshPreservesTheLastReadingAndTheButtonCanRecover()
    {
        var original = Reading();
        await using var fixture = await Fixture.CreateAsync(original);
        fixture.Refresh.FailNext = true;
        await using var renderer = fixture.Renderer();
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            await renderer.DispatchAsync(renderer.RefreshButton(root).EventId);
            Assert.Same(original, fixture.Snapshot.Current);
            Assert.Contains("Could not refresh inverter readings. Showing the last successful reading", renderer.Text(root));
            Assert.Contains("4.10", renderer.Text(root));
            Assert.False(renderer.RefreshButton(root).Disabled);
            fixture.Refresh.Next = Reading(0, 0, 0, 0) with { Timestamp = Timestamp.AddMinutes(5) };
            await renderer.DispatchAsync(renderer.RefreshButton(root).EventId);
            Assert.DoesNotContain("Could not refresh", renderer.Text(root));
            Assert.DoesNotContain("4.10", renderer.Text(root));
            Assert.DoesNotContain("Awaiting reading", renderer.Text(root));
            Assert.Contains("Idle", renderer.Text(root));
            Assert.Equal(2, fixture.Refresh.Calls);
            Assert.DoesNotMatch("[\\u0400-\\u04ff]", renderer.Text(root));
        });
        await fixture.AssertNoMutationsAsync();
    }

    [SqlServerFact]
    public async Task AnEmptyDashboardKeepsBothChartsAndCanFetchItsFirstReading()
    {
        await using var fixture = await Fixture.CreateAsync(null);
        fixture.Refresh.FailNext = true;
        await using var renderer = fixture.Renderer();
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            Assert.Contains("Waiting for the first inverter reading", renderer.Text(root));
            Assert.Contains("dashboard-generation", renderer.Attributes(root, "data-testid"));
            Assert.Contains("dashboard-sales", renderer.Attributes(root, "data-testid"));
            Assert.Contains("—", renderer.Text(root));
            Assert.DoesNotContain("Idle", renderer.Text(root));
            await renderer.DispatchAsync(renderer.RefreshButton(root).EventId);
            Assert.Contains("Could not fetch inverter readings. Please try again.", renderer.Text(root));
            fixture.Refresh.Next = Reading();
            await renderer.DispatchAsync(renderer.RefreshButton(root).EventId);
            Assert.DoesNotContain("Waiting for the first inverter reading", renderer.Text(root));
            Assert.Contains("Solar generation", renderer.Text(root));
            Assert.Contains("4.10", renderer.Text(root));
            Assert.Contains("Quick Actions", renderer.Text(root));
        });
        await fixture.AssertNoMutationsAsync();
    }

    [SqlServerFact]
    public async Task DisposalCancelsPendingRefreshAndIgnoresLateResponsesAndSnapshotNotifications()
    {
        await using var fixture = await Fixture.CreateAsync(Reading());
        fixture.Refresh.Hold = true;
        var renderer = fixture.Renderer();
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            var pending = renderer.DispatchAsync(renderer.RefreshButton(root).EventId);
            await renderer.DisposeAsync();
            Assert.True(fixture.Refresh.LastToken.IsCancellationRequested);
            var displays = renderer.Displays;
            var reads = fixture.Rules.Reads;
            fixture.Refresh.Complete(Reading(1));
            await pending;
            fixture.Snapshot.Update(Reading(2));
            Assert.Equal(displays, renderer.Displays);
            Assert.Equal(reads, fixture.Rules.Reads);
        });
        await fixture.AssertNoMutationsAsync();
    }

    [SqlServerFact]
    public async Task BackgroundReadingsClearARefreshFailureWithoutAnotherManualFetch()
    {
        await using var fixture = await Fixture.CreateAsync(Reading());
        await using var renderer = fixture.Renderer();
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            fixture.Refresh.FailNext = true;
            await renderer.DispatchAsync(renderer.RefreshButton(root).EventId);
            Assert.Contains("Could not refresh inverter readings", renderer.Text(root));
            fixture.Snapshot.Update(Reading(55, -2000, 450, 3300));
            await renderer.WaitForAsync(() => fixture.Rules.Reads >= 2 && renderer.TextByTestId(root, "solar-generation").Contains("3.30", StringComparison.Ordinal));
            var solar = renderer.TextByTestId(root, "solar-generation");
            Assert.Contains("3.30", solar);
            Assert.DoesNotContain("4.10", solar);
            Assert.DoesNotContain("2,000", solar);
            Assert.Contains("2,000", renderer.TextByTestId(root, "battery-power"));
            Assert.Contains("Importing", renderer.Text(root));
            Assert.DoesNotContain("Could not refresh inverter readings", renderer.Text(root));
            Assert.Equal(1, fixture.Refresh.Calls);
        });
        await fixture.AssertNoMutationsAsync();
    }

    [SqlServerFact]
    public async Task InitializationDoesNotLoseASnapshotPublishedWhileRulesAreLoading()
    {
        await using var fixture = await Fixture.CreateAsync(Reading());
        fixture.Rules.HoldFirst = true;
        await using var renderer = fixture.Renderer();
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var mounting = renderer.MountAsync();
            await fixture.Rules.FirstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            fixture.Snapshot.Update(Reading(55, -2000, 450, 3300));
            fixture.Rules.Release();
            var root = await mounting;
            var solar = renderer.TextByTestId(root, "solar-generation");
            Assert.Contains("3.30", solar);
            Assert.DoesNotContain("2,000", solar);
            Assert.DoesNotContain("4.10", solar);
            Assert.Contains("2,000", renderer.TextByTestId(root, "battery-power"));
            Assert.Equal(0, fixture.Refresh.Calls);
        });
        await fixture.AssertNoMutationsAsync();
    }

    private static void AssertOrdered(string html, params string[] fragments)
    {
        var previous = -1;
        foreach (var fragment in fragments)
        {
            var index = html.IndexOf(fragment, previous + 1, StringComparison.Ordinal);
            Assert.True(index > previous, $"Missing or out-of-order dashboard content: {fragment}");
            previous = index;
        }
    }

    private sealed class Fixture(Factory factory, InverterDataSnapshot snapshot, RefreshService refresh, RuleRepository rules,
        SocketController sockets, ServiceProvider services) : IAsyncDisposable
    {
        public InverterDataSnapshot Snapshot { get; } = snapshot;
        public RefreshService Refresh { get; } = refresh;
        public RuleRepository Rules { get; } = rules;
        public ServiceProvider Services { get; } = services;
        public EventRenderer Renderer() => new(Services, Services.GetRequiredService<ILoggerFactory>());
        public static async Task<Fixture> CreateAsync(InverterData? initial)
        {
            var connection = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SOLAR_TEST_SQL_CONNECTION"))
            { InitialCatalog = "SolarDashboardTests_" + Guid.NewGuid().ToString("N") };
            var factory = new Factory(new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlServer(connection.ConnectionString).Options);
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.MigrateAsync();
                await TestInstallation.EnsureAsync(db);
                db.AppSettings.AddRange(new AppSetting { Section = "Display", Key = "TimeZoneId", Value = "Europe/Warsaw" },
                    new AppSetting { Section = "Neighbor", Key = "preserved", Value = "unchanged" });
                await db.SaveChangesAsync();
            }
            var snapshot = new InverterDataSnapshot();
            if (initial is not null) snapshot.Update(initial);
            var refresh = new RefreshService(snapshot);
            var rules = new RuleRepository();
            var sockets = new SocketController();
            var services = ComponentServices();
            services.AddSingleton(snapshot);
            services.AddSingleton(new DeviceStatusSnapshot());
            services.AddSingleton<IInverterRefreshService>(refresh);
            services.AddSingleton<IConfigurationRules>(rules);
            services.AddSingleton<ISocketController>(sockets);
            services.AddSingleton<ISocketInventoryService>(sockets);
            services.AddSingleton<ISmartSocketCatalog>(sockets);
            services.AddSingleton<ISocketCommandTracker>(sockets);
            services.AddSingleton<IDbContextFactory<DeyeSolarDbContext>>(factory);
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddSingleton<AppSettingsService>();
            services.AddSingleton<IAppSettingsReader>(provider => provider.GetRequiredService<AppSettingsService>());
            services.AddSingleton<IAppSettingsWriter>(provider => provider.GetRequiredService<AppSettingsService>());
            services.AddSingleton<ISolarHistoryService, HistoryService>();
            services.AddSingleton<IExportSalesService, SalesService>();
            services.Configure<InverterConnectionOptions>(options => options.DeviceKey = "test-device");
            services.AddOptions<SolarEstimateOptions>();
            services.AddSingleton<ISolarRadiationSource, UnusedRadiationSource>();
            services.AddSingleton<ISolarEstimateStore, UnusedEstimateStore>();
            services.AddSingleton<SolarEstimateService>();
            return new(factory, snapshot, refresh, rules, sockets, services.BuildServiceProvider());
        }
        public async Task AssertNoMutationsAsync()
        {
            Assert.Equal(0, sockets.Mutations);
            Assert.Equal(0, Rules.Mutations);
            await using var db = factory.CreateDbContext();
            Assert.Equal(2, await db.AppSettings.CountAsync());
            Assert.Equal("unchanged", (await db.AppSettings.SingleAsync(row => row.Section == "Neighbor")).Value);
            Assert.Empty(await db.Readings.ToListAsync());
            Assert.Empty(await db.ExportReadings.ToListAsync());
            Assert.Empty(await db.RuleRunLogs.ToListAsync());
        }
        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await using var db = factory.CreateDbContext();
            await db.Database.EnsureDeletedAsync();
        }
    }

    private sealed class Factory(DbContextOptions<DeyeSolarDbContext> options) : IDbContextFactory<DeyeSolarDbContext>
    {
        public DeyeSolarDbContext CreateDbContext() => new(options, TestInstallation.Id);
        public Task<DeyeSolarDbContext> CreateDbContextAsync(CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(CreateDbContext()); }
    }

    private sealed class RefreshService(InverterDataSnapshot snapshot) : IInverterRefreshService
    {
        public int Calls { get; private set; }
        public bool FailNext { get; set; }
        public bool Hold { get; set; }
        public InverterData Next { get; set; } = Reading();
        public InverterData? LastResult { get; private set; }
        public CancellationToken LastToken { get; private set; }
        private TaskCompletionSource<InverterData>? _pending;
        public Task<InverterData> RefreshAsync(CancellationToken ct)
        {
            Calls++;
            LastToken = ct;
            if (FailNext) { FailNext = false; throw new InvalidOperationException("Synthetic inverter failure."); }
            if (Hold)
            {
                _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return _pending.Task;
            }
            LastResult = Next;
            snapshot.Update(Next);
            return Task.FromResult(Next);
        }
        public void Complete(InverterData reading)
        {
            // Deliberately complete after cancellation to test the component's disposal fence.
            LastResult = reading;
            snapshot.Update(reading);
            _pending!.SetResult(reading);
        }
    }

    private sealed class RuleRepository : IRuleRepository
    {
        public int Reads { get; private set; }
        public int Mutations { get; private set; }
        public bool HoldFirst { get; set; }
        public TaskCompletionSource FirstReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<List<TriggerRule>> _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static List<TriggerRule> Rules() => [new() { Id = 17, Name = "Independent garden socket", EntityId = "e0b2fc78-61cd-421a-b7a3-ed43121a0daa", CurrentState = false }];
        public Task<List<TriggerRule>> GetAllAsync(CancellationToken ct)
        {
            Reads++;
            FirstReadStarted.TrySetResult();
            return HoldFirst && Reads == 1 ? _pending.Task : Task.FromResult(Rules());
        }
        public void Release() => _pending.SetResult(Rules());
        public Task<TriggerRule?> GetByIdAsync(int id, CancellationToken ct) => Task.FromResult<TriggerRule?>(null);
        public Task<TriggerRule> CreateAsync(TriggerRule rule, CancellationToken ct) { Mutations++; return Task.FromResult(rule); }
        public Task UpdateAsync(TriggerRule rule, CancellationToken ct) { Mutations++; return Task.CompletedTask; }
        public Task RecordEvaluationAsync(int ruleId, DateTime when, CancellationToken ct) { Mutations++; return Task.CompletedTask; }
        public Task DeleteAsync(int id, string configurationVersion, CancellationToken ct) { Mutations++; return Task.CompletedTask; }
    }

    private sealed class SocketController : ISocketController, ISocketInventoryService, ISmartSocketCatalog, ISocketCommandTracker
    {
        public int Mutations { get; private set; }
        public Task TurnOnAsync(string id, CancellationToken ct) { Mutations++; return Task.CompletedTask; }
        public Task TurnOffAsync(string id, CancellationToken ct) { Mutations++; return Task.CompletedTask; }
        public Task<bool> GetStateAsync(string id, CancellationToken ct) => Task.FromResult(false);
        public Task<IReadOnlyList<DevicePowerInfo>> GetCachedDevicesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DevicePowerInfo>>([]);
        public Task<IReadOnlyList<DevicePowerInfo>> RefreshDevicesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DevicePowerInfo>>([]);
        public Task<SocketInventorySnapshot> ReadInventoryAsync(bool forceRefresh, CancellationToken ct) => Task.FromResult(new SocketInventorySnapshot([], [], Timestamp));
        public Task<ISmartSocket> GetAsync(SocketId id, CancellationToken ct) => throw new InvalidOperationException("Dashboard rendering must not obtain a command handle.");
        public Task<SocketCommandResult> ReadResultAsync(SocketId deviceId, SocketCommandId commandId, CancellationToken ct) => throw new InvalidOperationException("Dashboard rendering must not reconcile commands.");
        public Task<IReadOnlyList<SocketCommandReceipt>> ListUnresolvedAsync(SocketId deviceId, CancellationToken ct) => Task.FromResult<IReadOnlyList<SocketCommandReceipt>>([]);
        public Task<SocketCommandReceipt> ReleaseAsync(SocketId deviceId, SocketCommandId commandId, CancellationToken ct) => throw new InvalidOperationException("Dashboard rendering must not release commands.");
    }

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Timestamp; }
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
    private sealed class UnusedRadiationSource : ISolarRadiationSource
    {
        public Task<SolarRadiationObservation> ReadAsync(SolarEstimateOptions options, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException("Rendering must not request weather data.");
    }
    private sealed class UnusedEstimateStore : ISolarEstimateStore
    {
        public Task<CachedSolarObservation?> LoadAsync(CancellationToken ct) => throw new NotSupportedException("Rendering must not read the estimate cache.");
        public Task SaveAsync(CachedSolarObservation observation, CancellationToken ct) => throw new NotSupportedException("Rendering must not write the estimate cache.");
        public Task<SolarActual?> FindActualAsync(DateTimeOffset timestamp, int toleranceSeconds, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException("Rendering must not read inverter history.");
    }
    private sealed class HistoryService : ISolarHistoryService
    {
        public Task<SolarHistoryResult> ReadAsync(SolarHistoryPeriod period, CancellationToken ct, DateOnly? endDate = null) =>
            Task.FromResult(new SolarHistoryResult(Timestamp, Timestamp.AddHours(1), "Europe/Warsaw", []) { Today = new(2026, 9, 30), SelectedDate = new(2026, 9, 30) });
    }
    private sealed class SalesService : IExportSalesService
    {
        public Task<ExportSalesResult> ReadAsync(ExportSalesRequest request, CancellationToken ct) => Task.FromResult(new ExportSalesResult(
            request, new(2026, 9, 30), new(2026, 9, 28), "Europe/Warsaw", Timestamp, Timestamp.AddHours(1), [], null, null, null, null, 0, 0, 0));
    }

    private sealed class DashboardHost : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<DashboardPage>(1);
            builder.CloseComponent();
        }
    }

    // Exercise the real dashboard and its Blazor events, including duplicate event delivery.
    private sealed class EventRenderer(IServiceProvider services, ILoggerFactory loggerFactory) : Renderer(services, loggerFactory)
    {
        private TaskCompletionSource _displayChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Displays { get; private set; }
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
        {
            Displays++;
            var previous = _displayChanged;
            _displayChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
            return Task.CompletedTask;
        }
        protected override void HandleException(Exception exception) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
        public async Task<int> MountAsync()
        {
            var root = AssignRootComponentId(InstantiateComponent(typeof(DashboardHost)));
            await RenderRootComponentAsync(root);
            return root;
        }
        public Task DispatchAsync(ulong eventId) => DispatchEventAsync(eventId, null, new MouseEventArgs());
        public async Task WaitForAsync(Func<bool> condition)
        {
            while (!condition()) await _displayChanged.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        public (ulong EventId, bool Disabled) RefreshButton(int root) => Assert.Single(Buttons(root));
        private IEnumerable<(ulong EventId, bool Disabled)> Buttons(int componentId)
        {
            var frames = GetCurrentRenderTreeFrames(componentId);
            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames.Array[i];
                if (frame.FrameType == RenderTreeFrameType.Component)
                    foreach (var button in Buttons(frame.ComponentId)) yield return button;
                if (frame.FrameType != RenderTreeFrameType.Element || frame.ElementName != "button") continue;
                var children = frames.Array.Skip(i + 1).Take(frame.ElementSubtreeLength - 1).Where(child => child.FrameType == RenderTreeFrameType.Attribute).ToArray();
                if (!children.Any(child => child.AttributeName == "aria-label" && child.AttributeValue?.ToString() == "Refresh inverter data")) continue;
                yield return (children.Single(child => child.AttributeName == "onclick").AttributeEventHandlerId,
                    children.Any(child => child.AttributeName == "disabled" && child.AttributeValue is true));
            }
        }
        public IEnumerable<string?> Attributes(int componentId, string name)
        {
            var frames = GetCurrentRenderTreeFrames(componentId);
            foreach (var frame in frames.Array.Take(frames.Count))
            {
                if (frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == name) yield return frame.AttributeValue?.ToString();
                if (frame.FrameType == RenderTreeFrameType.Component)
                    foreach (var value in Attributes(frame.ComponentId, name)) yield return value;
            }
        }
        public string Text(int componentId)
        {
            var frames = GetCurrentRenderTreeFrames(componentId);
            return Text(frames.Array.Take(frames.Count));
        }
        public string TextByTestId(int componentId, string testId) => Assert.Single(ElementsByTestId(componentId, testId));
        private IEnumerable<string> ElementsByTestId(int componentId, string testId)
        {
            var frames = GetCurrentRenderTreeFrames(componentId);
            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames.Array[i];
                if (frame.FrameType == RenderTreeFrameType.Component)
                    foreach (var text in ElementsByTestId(frame.ComponentId, testId)) yield return text;
                if (frame.FrameType != RenderTreeFrameType.Element) continue;
                var subtree = frames.Array.Skip(i + 1).Take(frame.ElementSubtreeLength - 1);
                if (subtree.TakeWhile(child => child.FrameType == RenderTreeFrameType.Attribute)
                    .Any(child => child.AttributeName == "data-testid" && child.AttributeValue?.ToString() == testId))
                    yield return Text(subtree);
            }
        }
        private string Text(IEnumerable<RenderTreeFrame> frames)
            => WebUtility.HtmlDecode(string.Join(" ", frames.Select(frame => frame.FrameType switch
            {
                RenderTreeFrameType.Text => frame.TextContent,
                RenderTreeFrameType.Markup => frame.MarkupContent,
                RenderTreeFrameType.Component => Text(frame.ComponentId),
                _ => ""
            })));
    }

    private sealed class NullJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
