using SolarManagement.Inverters.Contracts;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Web.Api;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Web.Tests;

public class ExportSalesApiTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 27, 22, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Start.AddHours(24).AddMinutes(20);
    private static readonly string AuthenticationScheme = IdentityConstants.ApplicationScheme;
    private const string AuthenticationHeader = "X-Sales-Test-Identity";
    private const string AuthorizedIdentity = "authorized-synthetic-reader";

    [SqlServerFact]
    public async Task AuthenticatedHttpUsesConfiguredDeviceAndPersistedPricesForCalendarPeriods()
    {
        await using var host = await SalesHost.StartAsync();
        var before = await host.ReadStateAsync();
        var cases = new[]
        {
            (Query: "period=Day&date=2026-09-28", Period: ExportSalesPeriod.Day, Buckets: 24),
            (Query: "period=Month&date=2026-09-28", Period: ExportSalesPeriod.Month, Buckets: 30),
            (Query: "period=Year&date=2026-09-28", Period: ExportSalesPeriod.Year, Buckets: 12),
            (Query: "period=Custom&date=2026-09-28&from=2026-09-28&through=2026-09-28", Period: ExportSalesPeriod.Custom, Buckets: 1)
        };

        foreach (var item in cases)
        {
            using var response = await host.GetAsync(item.Query, AuthorizedIdentity);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            var result = Assert.IsType<ExportSalesResult>(await response.Content.ReadFromJsonAsync<ExportSalesResult>());

            Assert.Equal(item.Period, result.Request.Period);
            Assert.Equal(new DateOnly(2026, 9, 29), result.Today);
            Assert.Equal(new DateOnly(2026, 9, 28), result.ContractStartDate);
            Assert.Equal("Europe/Warsaw", result.TimeZoneId);
            Assert.Equal(24m, result.ExportKwh);
            Assert.Equal(24m, result.CreditedExportKwh);
            Assert.Equal(12m, result.EnergyValuePln);
            Assert.Equal(14.76m, result.EstimatedDepositPln);
            Assert.Equal((24, 24, 24), (result.ExpectedHours, result.ObservedHours, result.ValuedHours));
            Assert.False(result.IsPartial);
            Assert.Null(result.DataError);
            Assert.Null(result.PriceError);
            Assert.Equal(item.Buckets, result.Buckets.Count);
            Assert.Equal(24m, result.Buckets.Sum(bucket => bucket.ExportKwh));
            Assert.Equal(12m, result.Buckets.Sum(bucket => bucket.EnergyValuePln));
            Assert.Equal(Now, result.UpdatedAt);
            if (item.Period is ExportSalesPeriod.Month or ExportSalesPeriod.Year)
            {
                var current = Assert.IsType<ExportSaleProgress>(result.CurrentHour);
                Assert.Equal(Start.AddDays(1), current.Start);
                Assert.Equal(Start.AddDays(1).AddMinutes(10), current.ObservedThrough);
            }
            else Assert.Null(result.CurrentHour);
            if (item.Period == ExportSalesPeriod.Day)
            {
                Assert.Equal(Start, result.Start);
                Assert.Equal(Start.AddDays(1), result.End);
                Assert.All(result.Buckets, bucket =>
                {
                    Assert.Equal(1m, bucket.ExportKwh);
                    Assert.Equal(1m, bucket.CreditedExportKwh);
                    Assert.Equal(0.5m, bucket.EnergyValuePln);
                    Assert.Equal(0.615m, bucket.EstimatedDepositPln);
                });
            }
            else
            {
                Assert.Single(result.Buckets, bucket => bucket.ExpectedHours > 0);
                Assert.All(result.Buckets.Where(bucket => bucket.ExpectedHours == 0), bucket =>
                {
                    Assert.Null(bucket.ExportKwh);
                    Assert.Null(bucket.EnergyValuePln);
                    Assert.Null(bucket.EstimatedDepositPln);
                });
            }
        }

        using (var forgedDevice = await host.GetAsync("period=Day&date=2026-09-28&deviceSn=neighbor&device=neighbor", AuthorizedIdentity))
        {
            Assert.Equal(HttpStatusCode.OK, forgedDevice.StatusCode);
            var result = Assert.IsType<ExportSalesResult>(await forgedDevice.Content.ReadFromJsonAsync<ExportSalesResult>());
            Assert.Equal(24m, result.ExportKwh);
            Assert.Equal(12m, result.EnergyValuePln);
            Assert.Equal(14.76m, result.EstimatedDepositPln);
        }
        Assert.Equal(0, host.History.Calls);
        Assert.Equal(0, host.Prices.Calls);
        await host.AssertStateUnchangedAsync(before);
    }

    [SqlServerFact]
    public async Task CurrentHourHttpReadsPersistedProgressWithoutAddingItToCompletedTotals()
    {
        await using var host = await SalesHost.StartAsync(latestCurrentMinute: 15);
        var before = await host.ReadStateAsync();
        using (var response = await host.GetAsync("period=Day&date=2026-09-29&deviceSn=neighbor", AuthorizedIdentity))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = Assert.IsType<ExportSalesResult>(await response.Content.ReadFromJsonAsync<ExportSalesResult>());
            var current = Assert.IsType<ExportSaleProgress>(result.CurrentHour);
            Assert.Equal(Start.AddDays(1), current.Start);
            Assert.Equal(Start.AddDays(1).AddMinutes(15), current.ObservedThrough);
            Assert.Equal(900, current.ObservedSeconds);
            Assert.Equal(0.25m, current.ExportKwh);
            Assert.Equal(0.25m, current.CreditedExportKwh);
            Assert.Equal(0.125m, current.EnergyValuePln);
            Assert.Equal(0.15375m, current.EstimatedDepositPln);
            Assert.Equal(Now, result.UpdatedAt);
            Assert.Equal((0, 0, 0), (result.ExpectedHours, result.ObservedHours, result.ValuedHours));
            Assert.Null(result.ExportKwh);
            Assert.Null(result.EnergyValuePln);
            Assert.All(result.Buckets, bucket => Assert.Null(bucket.ExportKwh));
        }
        await host.AssertStateUnchangedAsync(before);

        await using (var db = host.Factory.CreateDbContext())
        {
            for (var minute = 20; minute <= 65; minute += 5)
                db.ExportReadings.Add(new ExportReading
                {
                    DeviceSn = "selected", ObservedAt = Start.AddDays(1).AddMinutes(minute).UtcDateTime,
                    GridPowerWatts = -1000, PolledAt = Start.AddDays(1).AddHours(1).AddMinutes(10).UtcDateTime
                });
            await db.SaveChangesAsync();
        }
        host.Clock.Current = Start.AddDays(1).AddHours(1).AddMinutes(10);
        var afterPolling = await host.ReadStateAsync();
        using (var response = await host.GetAsync("period=Day&date=2026-09-29", AuthorizedIdentity))
        {
            var result = Assert.IsType<ExportSalesResult>(await response.Content.ReadFromJsonAsync<ExportSalesResult>());
            Assert.Equal((1, 1, 1), (result.ExpectedHours, result.ObservedHours, result.ValuedHours));
            Assert.Equal(1m, result.ExportKwh);
            Assert.Equal(0.5m, result.EnergyValuePln);
            Assert.Equal(0.615m, result.EstimatedDepositPln);
            Assert.Equal(1m, result.Buckets[0].ExportKwh);
            Assert.Null(result.Buckets[1].ExportKwh);
            var current = Assert.IsType<ExportSaleProgress>(result.CurrentHour);
            Assert.Equal(Start.AddDays(1).AddHours(1), current.Start);
            Assert.Equal(Start.AddDays(1).AddMinutes(65), current.ObservedThrough);
            Assert.Equal(host.Clock.Current, result.UpdatedAt);
        }
        Assert.Equal(0, host.History.Calls);
        var afterRequest = await host.ReadStateAsync();
        AssertStateEqual(afterPolling, afterRequest);
        Assert.Equal(before.Readings.Where(row => row.Device == "neighbor"), afterRequest.Readings.Where(row => row.Device == "neighbor"));
    }

    [SqlServerFact]
    public async Task HourRolloverCannotUseAnotherDeviceToBridgeMissingMeasurements()
    {
        await using var host = await SalesHost.StartAsync(latestCurrentMinute: 15);
        host.Clock.Current = Start.AddDays(1).AddHours(1).AddMinutes(10);
        await using (var db = host.Factory.CreateDbContext())
        {
            for (var minute = 20; minute <= 65; minute += 5)
                db.ExportReadings.Add(new ExportReading
                {
                    DeviceSn = "neighbor", ObservedAt = Start.AddDays(1).AddMinutes(minute).UtcDateTime,
                    GridPowerWatts = -9000, PolledAt = host.Clock.Current.UtcDateTime
                });
            await db.SaveChangesAsync();
        }
        var before = await host.ReadStateAsync();

        using var response = await host.GetAsync("period=Day&date=2026-09-29&deviceSn=neighbor", AuthorizedIdentity);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = Assert.IsType<ExportSalesResult>(await response.Content.ReadFromJsonAsync<ExportSalesResult>());

        Assert.Equal((1, 0, 0), (result.ExpectedHours, result.ObservedHours, result.ValuedHours));
        Assert.Null(result.ExportKwh);
        Assert.Null(result.EnergyValuePln);
        var current = Assert.IsType<ExportSaleProgress>(result.CurrentHour);
        Assert.Null(current.ObservedThrough);
        Assert.Null(current.ExportKwh);
        Assert.Null(current.EnergyValuePln);
        Assert.NotNull(result.DataError);
        Assert.Equal(1, host.History.Calls);
        Assert.Equal(0, host.Prices.Calls);
        AssertStateEqual(before, await host.ReadStateAsync());
    }

    [SqlServerFact]
    public async Task AnonymousForgedAndInvalidHttpRequestsCannotReadOrMutateSql()
    {
        await using var host = await SalesHost.StartAsync();
        var before = await host.ReadStateAsync();

        foreach (var identity in new string?[] { null, "forged-synthetic-reader" })
        {
            var commandsBefore = host.Factory.DataCommands.Commands;
            using var response = await host.GetAsync("period=Day&date=2026-09-28&deviceSn=neighbor", identity);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(commandsBefore, host.Factory.DataCommands.Commands);
            Assert.DoesNotContain("exportKwh", await response.Content.ReadAsStringAsync());
            await host.AssertStateUnchangedAsync(before);
        }

        string[] invalidQueries =
        [
            "",
            "period=Week&date=2026-09-28",
            "period=999&date=2026-09-28",
            "period=Day&date=2026-09-31",
            "period=Day&date=28.09.2026",
            "period=Day&date=2026-09-30",
            "period=Day&date=1999-12-31",
            "period=Custom&date=2026-09-28",
            "period=Custom&date=2026-09-28&from=invalid&through=2026-09-28",
            "period=Custom&date=2026-09-28&from=2026-09-29&through=2026-09-28",
            "period=Custom&date=2026-09-28&from=2025-09-27&through=2026-09-28",
            "period=Custom&date=2026-09-28&from=2026-09-28&through=2026-09-30"
        ];
        foreach (var query in invalidQueries)
        {
            var commandsBefore = host.Factory.DataCommands.Commands;
            using var response = await host.GetAsync(query, AuthorizedIdentity);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(commandsBefore, host.Factory.DataCommands.Commands);
            Assert.DoesNotContain("exportKwh", await response.Content.ReadAsStringAsync());
            await host.AssertStateUnchangedAsync(before);
        }
        Assert.Equal(0, host.History.Calls);
        Assert.Equal(0, host.Prices.Calls);
    }

    [SqlServerFact]
    public async Task AdditiveCsvAndHourlyDetailsRequireAuthenticationAndKeepExistingDataAndLegacyJson()
    {
        await using var host=await SalesHost.StartAsync();var before=await host.ReadStateAsync();
        using(var response=await host.GetAsync("period=Day&date=2026-09-28&details=true",AuthorizedIdentity))
        {
            Assert.Equal(HttpStatusCode.OK,response.StatusCode);
            var data=Assert.IsType<ExportSalesResult>(await response.Content.ReadFromJsonAsync<ExportSalesResult>());
            Assert.Equal(24,data.Hours!.Count);Assert.Empty(data.MissingPriceHours!);
            Assert.All(data.Hours,h=>{Assert.Equal(0.5m,h.MarketAveragePricePlnPerKwh);Assert.Equal(1m,h.ExportKwh);});
        }
        using(var response=await host.GetAsync("period=Day&date=2026-09-28",AuthorizedIdentity))
        {
            var json=await response.Content.ReadAsStringAsync();Assert.DoesNotContain("\"hours\"",json);Assert.DoesNotContain("marketAveragePrice",json);
        }
        using(var csv=await host.GetPathAsync("/api/sales.csv?period=Day&date=2026-09-28",AuthorizedIdentity))
        {
            Assert.Equal(HttpStatusCode.OK,csv.StatusCode);Assert.Equal("text/csv",csv.Content.Headers.ContentType?.MediaType);
            var text=await csv.Content.ReadAsStringAsync();Assert.Equal(25,text.Split('\n',StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.Contains("market_average_price_pln_per_kwh",text);
            Assert.All(text.Split('\n',StringSplitOptions.RemoveEmptyEntries).Skip(1),row=>
                Assert.Equal(0.5m,decimal.Parse(row.Trim().Split(',')[^1].Trim('"'),System.Globalization.CultureInfo.InvariantCulture)));
        }
        foreach(var path in new[]{"/api/sales.csv?period=Day&date=2026-09-28","/api/activity","/api/readings.csv","/api/solar/production?period=Today"})
        {
            using var denied=await host.GetPathAsync(path,null);Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);
        }
        using(var invalid=await host.GetPathAsync("/api/sales.csv?period=Day&date=2026-09-31",AuthorizedIdentity))
        {Assert.Equal(HttpStatusCode.BadRequest,invalid.StatusCode);Assert.Contains("validation",await invalid.Content.ReadAsStringAsync());}
        await host.AssertStateUnchangedAsync(before);Assert.Equal(0,host.History.Calls);Assert.Equal(0,host.Prices.Calls);
    }

    private sealed class SalesHost(SqlServerTestDatabase database, WebApplication application, HttpClient client, CountingFactory factory,
        RejectingHistory history, RejectingPrices prices, FixedClock clock, int latestCurrentMinute) : IAsyncDisposable
    {
        public CountingFactory Factory { get; } = factory;
        public RejectingHistory History { get; } = history;
        public RejectingPrices Prices { get; } = prices;
        public FixedClock Clock { get; } = clock;

        public static async Task<SalesHost> StartAsync(int latestCurrentMinute = 10)
        {
            var database = await SqlServerTestDatabase.CreateAsync("ExportSalesHttpTests", SqlTestSchema.Model);
            var factory = new CountingFactory(database.Options);
            await using var owner = factory.CreateDbContext();
            WebApplication? application = null;
            try
            {
                await TestInstallation.EnsureAsync(owner);
                await SeedAsync(owner, latestCurrentMinute);
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions
                {
                    EnvironmentName = "Testing",
                    ContentRootPath = AppContext.BaseDirectory
                });
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Logging.ClearProviders();
                builder.Services.AddAuthentication(AuthenticationScheme)
                    .AddScheme<AuthenticationSchemeOptions, SyntheticAuthentication>(AuthenticationScheme, _ => { })
                    .AddScheme<AuthenticationSchemeOptions, MobileBearerAuthenticationHandler>(MobileBearerAuthenticationHandler.SchemeName, _ => { });
                builder.Services.AddSingleton<MobileSessionStore>();
            builder.Services.AddSingleton<DeyeSolar.Web.Auth.IAccountSessionStore>(p => p.GetRequiredService<MobileSessionStore>());
                builder.Services.AddAuthorization();
                builder.Services.AddRateLimiter(options => options.AddPolicy("price-check", context =>
                    System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("fixture")));
                builder.Services.AddSingleton<IDbContextFactory<DeyeSolarDbContext>>(factory);
                builder.Services.AddScoped(_ => factory.CreateDbContext());
                builder.Services.AddIdentityCore<IdentityUser>().AddEntityFrameworkStores<DeyeSolarDbContext>();
                builder.Services.AddScoped<InstallationMembershipService>();
                var clock = new FixedClock();
                builder.Services.AddSingleton<TimeProvider>(clock);
                builder.Services.AddSingleton<IOptionsMonitor<SolarSalesOptions>>(new FixedOptionsMonitor<SolarSalesOptions>(new()));
                builder.Services.AddSingleton<IOptionsMonitor<InverterConnectionOptions>>(new FixedOptionsMonitor<InverterConnectionOptions>(new() { DeviceKey = "selected" }));
                builder.Services.AddSingleton<IExportReadingStore, ExportReadingStore>();
                builder.Services.AddSingleton<IExportPriceStore, ExportPriceStore>();
                var history = new RejectingHistory();
                var prices = new RejectingPrices();
                builder.Services.AddSingleton<IExportGridHistorySource>(history);
                builder.Services.AddSingleton<IExportPriceSource>(prices);
                builder.Services.AddSingleton<IExportSalesService, ExportSalesService>();
                application = builder.Build();
                application.UseAuthentication();
                application.UseAuthorization();
                application.UseRateLimiter();
                application.MapExportSalesApi();
                DeyeSolar.Web.Redesign.RedesignEndpoints.MapRedesignApi(application);
                await application.StartAsync();
                var addresses = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
                Assert.NotNull(addresses);
                var address = new Uri(Assert.Single(addresses.Addresses));
                Assert.Equal("127.0.0.1", address.Host);
                Assert.NotEqual(0, address.Port);
                var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
                {
                    BaseAddress = address,
                    Timeout = TimeSpan.FromSeconds(20)
                };
                return new(database, application, client, factory, history, prices, clock, latestCurrentMinute);
            }
            catch
            {
                await TestHttpHostCleanup.DisposeAsync(application, database);
                throw;
            }
        }

        public async Task<HttpResponseMessage> GetAsync(string query, string? identity)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/sales?" + query);
            if (identity is not null) request.Headers.Add(AuthenticationHeader, identity);
            return await client.SendAsync(request);
        }

        public async Task<HttpResponseMessage> GetPathAsync(string path,string? identity)
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,path);
            if(identity is not null)request.Headers.Add(AuthenticationHeader,identity);
            return await client.SendAsync(request);
        }

        public async Task<DatabaseState> ReadStateAsync()
        {
            await using var db = Factory.CreateDbContext();
            return new(
                await db.ExportReadings.AsNoTracking().OrderBy(row => row.DeviceSn).ThenBy(row => row.ObservedAt)
                    .Select(row => new ReadingState(row.DeviceSn, row.ObservedAt, row.GridPowerWatts, row.PolledAt)).ToArrayAsync(),
                await db.ExportPrices.AsNoTracking().OrderBy(row => row.StartUtc)
                    .Select(row => new PriceState(row.StartUtc, row.PricePlnPerMwh, row.RetrievedAtUtc)).ToArrayAsync(),
                await db.AppSettings.AsNoTracking().OrderBy(row => row.Id)
                    .Select(row => new SettingState(row.Id, row.Section, row.Key, row.Value)).ToArrayAsync(),
                await db.Readings.AsNoTracking().OrderBy(row => row.Id)
                    .Select(row => new LegacyState(row.Id, row.Timestamp, row.GridConsumption, row.DataSource)).ToArrayAsync(),
                await db.RuleRunLogs.AsNoTracking().OrderBy(row => row.Id)
                    .Select(row => new RuleState(row.Id, row.Timestamp, row.RuleName, row.Action)).ToArrayAsync());
        }

        public async Task AssertStateUnchangedAsync(DatabaseState expected)
        {
            var actual = await ReadStateAsync();
            Assert.Equal(expected.Readings, actual.Readings);
            Assert.Equal(expected.Prices, actual.Prices);
            Assert.Equal(expected.Settings, actual.Settings);
            Assert.Equal(expected.Legacy, actual.Legacy);
            Assert.Equal(expected.Rules, actual.Rules);
            var perDevice = latestCurrentMinute == 15 ? 294 : 293;
            Assert.Equal(perDevice * 2, actual.Readings.Length);
            Assert.Equal(perDevice, actual.Readings.Count(row => row.Device == "selected"));
            Assert.Equal(perDevice, actual.Readings.Count(row => row.Device == "neighbor"));
            Assert.All(actual.Readings.Where(row => row.Device == "neighbor"), row => Assert.Equal(-9000, row.Watts));
            Assert.Equal(102, actual.Prices.Length);
            Assert.Equal(12345.678901m, actual.Prices[0].Price);
            Assert.Equal(-9876.543210m, actual.Prices[^1].Price);
            Assert.Equal("preserved", Assert.Single(actual.Settings).Value);
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await TestHttpHostCleanup.DisposeAsync(application, database, stop: true);
        }
    }

    private static async Task SeedAsync(DeyeSolarDbContext db, int latestCurrentMinute)
    {
        for (var offset = -10; offset <= 24 * 60 + latestCurrentMinute; offset += 5)
        {
            var time = Start.AddMinutes(offset).UtcDateTime;
            db.ExportReadings.AddRange(
                new ExportReading { DeviceSn = "selected", ObservedAt = time, GridPowerWatts = -1000, PolledAt = Now.UtcDateTime },
                new ExportReading { DeviceSn = "neighbor", ObservedAt = time, GridPowerWatts = -9000, PolledAt = Now.UtcDateTime });
        }
        for (var quarter = 0; quarter < 100; quarter++)
            db.ExportPrices.Add(new ExportPriceRow
            {
                StartUtc = Start.AddMinutes(quarter * 15).UtcDateTime,
                PricePlnPerMwh = 500m,
                RetrievedAtUtc = Now.UtcDateTime
            });
        db.ExportPrices.AddRange(
            new ExportPriceRow { StartUtc = Start.AddMinutes(-15).UtcDateTime, PricePlnPerMwh = 12345.678901m, RetrievedAtUtc = Now.UtcDateTime },
            new ExportPriceRow { StartUtc = Start.AddDays(1).AddHours(1).UtcDateTime, PricePlnPerMwh = -9876.543210m, RetrievedAtUtc = Now.UtcDateTime });
        db.AppSettings.Add(new AppSetting { Section = "Neighbor", Key = "unchanged", Value = "preserved" });
        db.Readings.Add(new Reading { Timestamp = Start.UtcDateTime, GridConsumption = -50000, DataSource = "legacy-neighbor" });
        db.RuleRunLogs.Add(new RuleRunLog { Timestamp = Start.UtcDateTime, RuleName = "neighbor-rule", Action = "unchanged" });
        await db.SaveChangesAsync();
    }

    private sealed record ReadingState(string Device, DateTime ObservedAt, int Watts, DateTime PolledAt);
    private sealed record PriceState(DateTime Start, decimal Price, DateTime RetrievedAt);
    private sealed record SettingState(int Id, string Section, string Key, string Value);
    private sealed record LegacyState(int Id, DateTime Timestamp, int Watts, string Source);
    private sealed record RuleState(int Id, DateTime Timestamp, string Name, string Action);
    private sealed record DatabaseState(ReadingState[] Readings, PriceState[] Prices, SettingState[] Settings, LegacyState[] Legacy, RuleState[] Rules);

    private static void AssertStateEqual(DatabaseState expected, DatabaseState actual)
    {
        Assert.Equal(expected.Readings, actual.Readings);
        Assert.Equal(expected.Prices, actual.Prices);
        Assert.Equal(expected.Settings, actual.Settings);
        Assert.Equal(expected.Legacy, actual.Legacy);
        Assert.Equal(expected.Rules, actual.Rules);
    }

    private sealed class CountingFactory(DbContextOptions<DeyeSolarDbContext> options) : IDbContextFactory<DeyeSolarDbContext>
    {
        public SolarDataCommandCounter DataCommands { get; } = new();
        public DeyeSolarDbContext CreateDbContext()
        {
            return new(new DbContextOptionsBuilder<DeyeSolarDbContext>(options).AddInterceptors(DataCommands).Options, TestInstallation.Id);
        }
        public Task<DeyeSolarDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = Now;
        public override DateTimeOffset GetUtcNow() => Current;
    }


    private sealed class RejectingHistory : IExportGridHistorySource
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Task<IReadOnlyList<ExportGridSample>> ReadAsync(string deviceSn, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("Complete SQL readings must avoid the external history provider.");
        }
    }

    private sealed class RejectingPrices : IExportPriceSource
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Task<IReadOnlyList<ExportPriceInterval>> ReadAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("Complete SQL prices must avoid the external price provider.");
        }
    }

    // This identity is synthetic and confined to this loopback test host; production authentication is unchanged.
    private sealed class SyntheticAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(AuthenticationHeader, out var identity))
                return Task.FromResult(AuthenticateResult.NoResult());
            if (identity.Count != 1 || identity[0] != AuthorizedIdentity)
                return Task.FromResult(AuthenticateResult.Fail("Unknown synthetic test identity."));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "synthetic-reader"), new Claim(ClaimTypes.Name, "synthetic-reader"),
                    new Claim(InstallationIds.ClaimType, TestInstallation.Id)],
                AuthenticationScheme));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, AuthenticationScheme)));
        }
    }
}
