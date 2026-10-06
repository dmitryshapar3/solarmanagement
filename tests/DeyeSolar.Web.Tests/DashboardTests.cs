using SolarManagement.Inverters.Contracts;
using SolarManagement.SmartSockets.Contracts;
using Watts = SolarManagement.Inverters.Contracts.Watts;
using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Localization;
using DeyeSolar.Web.Redesign;
using Microsoft.AspNetCore.Http;
using DeyeSolar.Web.Services;
using DeyeSolar.Web.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
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
    public async Task NavigationUsesTheNewProductRoutesWithAccessibleEnglishLabels()
    {
        await using var services = ComponentServices().BuildServiceProvider();
        var html = await RenderAsync<NavMenu>(services);
        foreach (var route in new[] { "/", "/energy", "/devices", "/automations", "/activity", "/settings" }) Assert.Contains($"href=\"{route}\"", html);
        Assert.Contains("Main navigation", html); Assert.Contains("Energy", html); Assert.Contains("Automations", html);
        Assert.DoesNotContain("href=\"/generation\"", html); Assert.DoesNotContain("href=\"/rules\"", html);
        Assert.DoesNotMatch("[\\u0400-\\u04ff]", html);
    }
    [Fact]
    public void ProductRoutesRetainInheritedAuthorization()
    {
        foreach (var page in new[] { typeof(DashboardPage), typeof(DeyeSolar.Web.Pages.Energy), typeof(DeyeSolar.Web.Pages.EnergyExport), typeof(DeyeSolar.Web.Pages.Automations), typeof(DeyeSolar.Web.Pages.Activity), typeof(DeyeSolar.Web.Pages.Devices), typeof(DeyeSolar.Web.Pages.Settings) })
        {
            Assert.NotEmpty(page.GetCustomAttributes(true).OfType<IAuthorizeData>());
            Assert.Empty(page.GetCustomAttributes(true).OfType<IAllowAnonymous>());
        }
    }
    private static ServiceCollection ComponentServices()
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddComponentLocalization();
        services.AddSingleton<IJSRuntime, NullJsRuntime>(); services.AddSingleton<TimeProvider>(new Clock());
        services.AddSingleton<NavigationManager, TestNavigation>(); return services;
    }
    private static async Task<string> RenderAsync<T>(IServiceProvider services) where T : IComponent
    {
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => WebUtility.HtmlDecode((await renderer.RenderComponentAsync<T>()).ToHtmlString()));
    }
    [SqlServerTheory]
    [InlineData(-2742, 4100, "4.10")]
    [InlineData(2742, 4100, "4.10")]
    [InlineData(2742, 0, "0.00")]
    public async Task HomeSolarPowerRemainsIndependentOfBatteryFlow(int battery, int solar, string expected)
    {
        await using var fixture = await Fixture.CreateAsync(Reading() with { SolarProduction = solar, BatteryPower = battery });
        var html = await RenderAsync<DashboardHost>(fixture.Services);
        var actual = Regex.Match(html, "data-testid=\"solar-actual\"[^>]*>[\\s\\S]*?</div>").Value;
        Assert.Contains(expected, actual); Assert.DoesNotContain("2,742", actual);
        Assert.Contains("Export today", html); Assert.Contains("Smart plugs", html); Assert.Contains("Recent activity", html);
        Assert.DoesNotContain("Refresh inverter data", html); Assert.DoesNotContain("mud-", html);
        Assert.Equal(0, fixture.Refresh.Calls); await fixture.AssertNoMutationsAsync();
    }
    [SqlServerTheory]
    [InlineData(MeasurementQuality.Missing)]
    [InlineData(MeasurementQuality.Invalid)]
    [InlineData(MeasurementQuality.Stale)]
    [InlineData(MeasurementQuality.Good)]
    public async Task HomeDistinguishesMissingSolarFromConfirmedZero(MeasurementQuality quality)
    {
        var reading = Reading(grid: 0, solar: 0) with { Telemetry = ZeroFlows(quality) };
        await using var fixture = await Fixture.CreateAsync(reading); var html = await RenderAsync<DashboardHost>(fixture.Services);
        var actual = Regex.Match(html, "data-testid=\"solar-actual\"[^>]*>[\\s\\S]*?</div>").Value;
        Assert.Contains(quality == MeasurementQuality.Good ? "0.00" : "—", actual);
        if (quality != MeasurementQuality.Good) Assert.DoesNotContain("0.00", actual);
        await fixture.AssertNoMutationsAsync();
    }
    [SqlServerFact]
    public async Task EmptyHomeKeepsMeasuredValuesUnavailableAndNeverRequestsHardware()
    {
        await using var fixture = await Fixture.CreateAsync(null); var html = await RenderAsync<DashboardHost>(fixture.Services);
        Assert.Contains("Awaiting reading", html); Assert.Contains("Waiting for readings", html); Assert.Contains("No smart plugs yet", html);
        Assert.Contains("View production", html); Assert.Contains("/activity", html); Assert.Equal(0, fixture.Refresh.Calls);
        await fixture.AssertNoMutationsAsync();
    }
    [SqlServerFact]
    public async Task HomeKeepsProvisionalExportSeparateFromCompletedValueAndDeposit()
    {
        var sales = new ExportSalesResult(new(ExportSalesPeriod.Day,new(2026,9,30)),new(2026,9,30),new(2026,9,28),
            "Europe/Warsaw",Timestamp,Timestamp.AddHours(1),[],5m,5m,10m,12.3m,1,1,1,
            CurrentHour:new(Timestamp,Timestamp.AddMinutes(5),1.25m,1.25m,2.5m,3.075m,300));
        await using var fixture = await Fixture.CreateAsync(Reading(),sales);
        var html = await RenderAsync<DashboardHost>(fixture.Services);
        Assert.Contains("5.00",Regex.Match(html,"data-testid=\"sales-export\"[^>]*>(.*?)</div>",RegexOptions.Singleline).Value);
        Assert.Contains("10.00",Regex.Match(html,"data-testid=\"sales-value\"[^>]*>(.*?)</strong>",RegexOptions.Singleline).Value);
        Assert.Contains("12.30",Regex.Match(html,"data-testid=\"sales-deposit\"[^>]*>(.*?)</strong>",RegexOptions.Singleline).Value);
        Assert.Contains("In progress: 1.25 kWh, excluded from totals",html);
        Assert.Contains("In progress: 2.50 PLN, excluded from totals",html);
        Assert.Contains("It is not a cash payout",html);
        await fixture.AssertNoMutationsAsync();
    }
    [SqlServerFact]
    public async Task ReadingsPageRendersTheLatestSnapshotInTheInstallationTimeZone()
    {
        await using var fixture = await Fixture.CreateAsync(Reading());
        var html = await RenderAsync<DeyeSolar.Web.Pages.ReadingsView>(fixture.Services);
        Assert.Contains("Live readings", html);
        Assert.Contains("Europe/Warsaw", html);
        Assert.Contains("4.10", html);
        Assert.Contains("Polled", html);
        Assert.DoesNotContain("Readings could not be loaded", html);
        Assert.DoesNotContain("Action failed", html);
        Assert.Equal(0, fixture.Refresh.Calls);
        await fixture.AssertNoMutationsAsync();
    }
    [SqlServerFact]
    public async Task InitialSnapshotCannotBeLostWhileRulesAreLoading()
    {
        await using var fixture = await Fixture.CreateAsync(Reading()); fixture.Rules.HoldFirst = true;
        await using var renderer = fixture.Renderer(); await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var mounting = renderer.MountAsync(); await fixture.Rules.FirstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            fixture.Snapshot.Update(Reading(55, -2000, 450, 3300)); fixture.Rules.Release(); var root = await mounting;
            var solar = renderer.TextByTestId(root, "solar-actual"); Assert.Contains("3.30", solar); Assert.DoesNotContain("4.10", solar);
            Assert.Contains("Smart plugs", renderer.Text(root)); Assert.Equal(0, fixture.Refresh.Calls);
        }); await fixture.AssertNoMutationsAsync();
    }
    [SqlServerFact]
    public async Task DisposedHomeUnsubscribesFromBackgroundSnapshotUpdates()
    {
        await using var fixture = await Fixture.CreateAsync(Reading()); var renderer = fixture.Renderer();
        await renderer.Dispatcher.InvokeAsync(async () => { await renderer.MountAsync(); }); await renderer.DisposeAsync();
        var displays = renderer.Displays; fixture.Snapshot.Update(Reading(solar: 2800));
        Assert.Equal(displays, renderer.Displays); Assert.Equal(0, fixture.Refresh.Calls); await fixture.AssertNoMutationsAsync();
    }

    [SqlServerTheory]
    [InlineData(0)]
    [InlineData(800)]
    public async Task FreshEstimateEventAfterTheParentSnapshotKeepsExpectedPowerVisible(double irradiance)
    {
        var clock = new EventClock();
        var source = new ModelRadiationSource(irradiance);
        var store = new MemoryEstimateStore();
        await using var fixture = await Fixture.CreateAsync(Reading(), configure: services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<ISolarRadiationSource>(source);
            services.AddSingleton<ISolarEstimateStore>(store);
            services.Configure<SolarEstimateOptions>(options =>
            {
                options.DeyeSolarPowerIsPvDcConfirmed = true;
                options.DeyeConfirmedDeviceSn = "test-device";
            });
        });
        var estimates = fixture.Services.GetRequiredService<SolarEstimateService>();
        await estimates.UpdateAsync(default);
        await using var renderer = fixture.Renderer();
        var root = await renderer.Dispatcher.InvokeAsync(renderer.MountAsync);
        await renderer.Dispatcher.InvokeAsync(() =>
        {
            Assert.DoesNotContain("—", renderer.TextByTestId(root, "solar-possible"));
            // Deliver the parent snapshot first, exactly as the scheduled runtime cycle does.
            fixture.Snapshot.Update(Reading());
        });
        var displays = renderer.Displays;
        clock.Now = Timestamp.AddSeconds(1);

        // Only the real estimate event can refresh the card: this clock never fires timers,
        // and no newer inverter/device event refreshes the parent's captured timestamp.
        await Task.Run(() => estimates.UpdateAsync(default));
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.WaitForAsync(() => renderer.Displays > displays);
            var current = Assert.IsType<SolarPowerEstimate>(estimates.Current.Estimate);
            Assert.Equal(clock.Now, current.Timestamp);
            var expected = current.CentralKw.ToString("F2", fixture.Services.GetRequiredService<UiText>().Culture);
            Assert.Contains(expected, renderer.TextByTestId(root, "solar-possible"));
            Assert.DoesNotContain("—", renderer.TextByTestId(root, "solar-possible"));
            Assert.DoesNotContain("The estimate is unavailable. Waiting for fresh data.", renderer.Text(root));
        });
        Assert.Equal(1, source.Calls);
        Assert.Equal(0, fixture.Refresh.Calls);
        await fixture.AssertNoMutationsAsync();
    }
    private static IInverterTelemetry ZeroFlows(MeasurementQuality quality) => new InverterTelemetry(new(Guid.NewGuid()), Timestamp,
        new(new Percent(87), Timestamp, MeasurementQuality.Good), new(new Watts(-2400), Timestamp, MeasurementQuality.Good),
        new(new Celsius(24), Timestamp, MeasurementQuality.Good), new(new Volts(51.5), Timestamp, MeasurementQuality.Good),
        new(new Amperes(4.2), Timestamp, MeasurementQuality.Good), new(new Watts(0), Timestamp, quality), new(new Watts(0), Timestamp, quality),
        new(new Watts(900), Timestamp, MeasurementQuality.Good), SolarManagement.Inverters.Contracts.SolarPowerBasis.PvDc);

    private sealed class Fixture(SqlServerTestDatabase database, Factory factory, InverterDataSnapshot snapshot, RefreshService refresh, RuleRepository rules,
        SocketController sockets, ServiceProvider services) : IAsyncDisposable
    {
        public InverterDataSnapshot Snapshot { get; } = snapshot;
        public RefreshService Refresh { get; } = refresh;
        public RuleRepository Rules { get; } = rules;
        public ServiceProvider Services { get; } = services;
        public EventRenderer Renderer() => new(Services, Services.GetRequiredService<ILoggerFactory>());
        public static async Task<Fixture> CreateAsync(InverterData? initial, ExportSalesResult? sales = null,
            Action<ServiceCollection>? configure = null)
        {
            var database = await SqlServerTestDatabase.CreateAsync("SolarDashboardTests", seed: async db =>
            {
                await TestInstallation.EnsureAsync(db);
                db.AppSettings.AddRange(new AppSetting { Section = "Display", Key = "TimeZoneId", Value = "Europe/Warsaw" },
                    new AppSetting { Section = "Neighbor", Key = "preserved", Value = "unchanged" });
                await db.SaveChangesAsync();
            });
            var factory = new Factory(database.Options);
            try
            {
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
                services.AddSingleton<IExportSalesService>(new SalesService(sales));
                services.Configure<InverterConnectionOptions>(options => options.DeviceKey = "test-device");
                services.AddOptions<SolarEstimateOptions>();
                services.AddSingleton<ISolarRadiationSource, UnusedRadiationSource>();
                services.AddSingleton<ISolarEstimateStore, UnusedEstimateStore>();
                services.AddSingleton<SolarEstimateService>();
                services.AddSingleton<IDeviceLabelStore, ReadOnlyLabels>(); services.AddSingleton<DeviceNameService>();
                services.AddSingleton<ISolarDayForecastSource, EmptyForecast>(); services.AddSingleton<ISolarHistoryStore, EmptyHistory>();
                services.AddSingleton<SolarProductionService>();
                var installation = new CurrentInstallation(); installation.BindOnce(TestInstallation.Id);
                var security = new InteractiveSecurityContext(new ReadOnlyAccess(), installation, new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "dashboard-fixture-account")], "test")) } });
                security.BindOnce(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "dashboard-fixture-account")], "test")));
                services.AddSingleton(security); services.AddSingleton<RedesignQueries>();
                configure?.Invoke(services);
                return new(database, factory, snapshot, refresh, rules, sockets, services.BuildServiceProvider());
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
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
            try { await Services.DisposeAsync(); }
            finally { await database.DisposeAsync(); }
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

    private sealed class ReadOnlyAccess : IInstallationAccessAuthorizer
    {
        public Task<InstallationMembership> CheckAsync(System.Security.Claims.ClaimsPrincipal actor, string installationId, InstallationPermission permission, CancellationToken ct = default)
        { Assert.Equal(InstallationPermission.Read, permission); return Task.FromResult(new InstallationMembership { InstallationId = installationId, UserId = "component-fixture", Role = "Owner" }); }
    }
    private sealed class ReadOnlyLabels : IDeviceLabelStore
    {
        public Task<Dictionary<string,string>> LoadAsync(CancellationToken ct) => Task.FromResult(new Dictionary<string,string>());
        public Task SaveAsync(Dictionary<string,string> labels, CancellationToken ct) => throw new InvalidOperationException("Rendering cannot change labels.");
    }
    private sealed class EmptyForecast : ISolarDayForecastSource
    {
        public Task<SolarDayForecast> ReadAsync(SolarEstimateOptions options, DateTimeOffset start, DateTimeOffset end, DateOnly selectedDate, CancellationToken ct) => Task.FromResult(new SolarDayForecast([], Timestamp, null, null, null));
    }
    private sealed class EmptyHistory : ISolarHistoryStore
    {
        public Task<IReadOnlyList<SolarActual>> ReadAsync(string deviceSn, DateTimeOffset start, DateTimeOffset end, CancellationToken ct) => Task.FromResult<IReadOnlyList<SolarActual>>([]);
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Timestamp; }
    private sealed class EventClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Timestamp;
        public override DateTimeOffset GetUtcNow() => Now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new IdleTimer();
        private sealed class IdleTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private sealed class ModelRadiationSource(double irradiance) : ISolarRadiationSource
    {
        public int Calls { get; private set; }
        public Task<SolarRadiationObservation> ReadAsync(SolarEstimateOptions options, DateTimeOffset now, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new SolarRadiationObservation(now, irradiance, irradiance / 2, 20, 2, now, 0)
            {
                Kind = SolarRadiationKind.WeatherModel, RetrievedAt = now,
                Forecast = new[] { -15, 0, 15, 30 }.Select(minutes =>
                    new SolarWeatherSample(now.AddMinutes(minutes), irradiance, irradiance / 2, 20, 2, 0)).ToArray()
            });
        }
    }
    private sealed class MemoryEstimateStore : ISolarEstimateStore
    {
        public Task<CachedSolarObservation?> LoadAsync(CancellationToken ct) => Task.FromResult<CachedSolarObservation?>(null);
        public Task SaveAsync(CachedSolarObservation observation, CancellationToken ct) => Task.CompletedTask;
        public Task<SolarActual?> FindActualAsync(DateTimeOffset timestamp, int toleranceSeconds, DateTimeOffset now, CancellationToken ct)
            => Task.FromResult<SolarActual?>(new(Timestamp.AddMinutes(-1), 4.1, DeyeSolar.Domain.Models.SolarPowerBasis.PvDc));
    }
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
    private sealed class SalesService(ExportSalesResult? fixture = null) : IExportSalesService
    {
        public Task<ExportSalesResult> ReadAsync(ExportSalesRequest request, CancellationToken ct) => Task.FromResult(fixture ?? new ExportSalesResult(
            request, new(2026, 9, 30), new(2026, 9, 28), "Europe/Warsaw", Timestamp, Timestamp.AddHours(1), [], null, null, null, null, 0, 0, 0));
    }

    private sealed class DashboardHost : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
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
