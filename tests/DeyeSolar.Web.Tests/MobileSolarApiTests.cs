using SolarPowerBasis = DeyeSolar.Domain.Models.SolarPowerBasis;
using SolarManagement.Inverters.Contracts;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using DeyeSolar.Web.Api;
using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Integrations;
using DeyeSolar.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Web.Tests;

public class MobileSolarApiTests
{
    private const string FixtureInstallation = "fixture-installation";
    private static readonly Guid EditableSocket = new("996356a7-c311-4e0d-9426-7a0df8b63b4b");
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 20, 0, TimeSpan.Zero);
    private const string Username = "mobile-api-reader";
    private const string Password = "LocalTestPassword!42";
    private static readonly string[] ProtectedRoutes =
    ["/api/sales?period=Day&date=2026-09-30", "/api/solar/estimate", "/api/solar/history?period=Today", "/api/dashboard/refresh"];

    [SqlServerFact]
    public async Task PasswordLoginRequiresVerifiedIdentityForEveryInstallation()
    {
        await using var host = await ApiHost.StartAsync();
        await using (var db = host.Factory.CreateDbContext())
        {
            var user = await db.Users.SingleAsync(user => user.UserName == Username);
            user.EmailConfirmed = false;
            user.PhoneNumberConfirmed = false;
            await db.SaveChangesAsync();
        }
        using var refused = await host.Client.PostAsJsonAsync("/api/auth/login", new MobileLoginRequest(Username, Password));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        await using (var db = host.Factory.CreateDbContext())
        {
            var user = await db.Users.SingleAsync(user => user.UserName == Username);
            user.PhoneNumber = "+48123456789";
            user.PhoneNumberConfirmed = true;
            await db.SaveChangesAsync();
        }
        Assert.NotNull(await host.LoginAsync());
    }

    [SqlServerFact]
    public async Task RuleUpdateToggleAndDeleteRequireTheClientsCurrentConfigurationVersion()
    {
        await using var host = await ApiHost.StartAsync();
        var session = await host.LoginAsync();
        host.Client.DefaultRequestHeaders.Authorization = new("Bearer", session.Token);
        var initial = new TriggerRuleRequest("Client snapshot", EditableSocket.ToString("D"), false, 80, false, 80, false, 3000, 15, 30, null, null)
        { SourceInverterId = null };
        using var createdResponse = await host.Client.PostAsJsonAsync("/api/rules", initial);
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = (await createdResponse.Content.ReadFromJsonAsync<TriggerRuleDto>())!;
        Assert.Matches("^[A-F0-9]{64}$", created.ConfigurationVersion);
        var route = $"/api/rules/{created.Id}";
        using (var missing = await host.Client.PutAsJsonAsync(route, initial with { Name = "Unconditional overwrite" }))
            Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        using (var missing = await host.Client.PatchAsJsonAsync(route + "/enabled", new { enabled = true }))
            Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        using (var missing = await host.Client.DeleteAsync(route))
            Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        using var changedResponse = await host.Client.PutAsJsonAsync(route, initial with
        { Name = "Concurrent saved edit", ConfigurationVersion = created.ConfigurationVersion });
        Assert.Equal(HttpStatusCode.OK, changedResponse.StatusCode);
        var changed = (await changedResponse.Content.ReadFromJsonAsync<TriggerRuleDto>())!;
        Assert.NotEqual(created.ConfigurationVersion, changed.ConfigurationVersion);
        using (var stale = await host.Client.PutAsJsonAsync(route, initial with { ConfigurationVersion = created.ConfigurationVersion }))
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using (var stale = await host.Client.PatchAsJsonAsync(route + "/enabled", new RuleEnabledRequest(false, created.ConfigurationVersion)))
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using (var staleRequest = new HttpRequestMessage(HttpMethod.Delete, route))
        {
            staleRequest.Headers.IfMatch.Add(new EntityTagHeaderValue('"' + created.ConfigurationVersion + '"'));
            using var stale = await host.Client.SendAsync(staleRequest);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        }
        using (var fresh = await host.Client.PatchAsJsonAsync(route + "/enabled", new RuleEnabledRequest(false, changed.ConfigurationVersion)))
            Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        using var read = await host.Client.GetAsync(route);
        var retained = (await read.Content.ReadFromJsonAsync<TriggerRuleDto>())!;
        Assert.Equal("Concurrent saved edit", retained.Name);
        using var delete = new HttpRequestMessage(HttpMethod.Delete, route);
        delete.Headers.IfMatch.Add(new EntityTagHeaderValue('"' + retained.ConfigurationVersion + '"'));
        using var removed = await host.Client.SendAsync(delete);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        using var absent = await host.Client.GetAsync(route);
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        using var unconditionalAbsentDelete = await host.Client.DeleteAsync(route);
        Assert.Equal((HttpStatusCode)428, unconditionalAbsentDelete.StatusCode);
        using var repeatedDelete = new HttpRequestMessage(HttpMethod.Delete, route);
        repeatedDelete.Headers.IfMatch.Add(new EntityTagHeaderValue('"' + retained.ConfigurationVersion + '"'));
        using var repeatedResponse = await host.Client.SendAsync(repeatedDelete);
        Assert.Equal(HttpStatusCode.NoContent, repeatedResponse.StatusCode);
    }

    [SqlServerFact]
    public async Task NullRuleFieldsAreClientErrorsAndInvalidTargetsCannotBeEnabledByAnyRuleMutation()
    {
        await using var host = await ApiHost.StartAsync();
        host.Client.DefaultRequestHeaders.Authorization = new("Bearer", (await host.LoginAsync()).Token);
        var initial = new TriggerRuleRequest("Valid draft", EditableSocket.ToString("D"), false, 80, false, 80, false, 3000, 15, 30, null, null)
        { SourceInverterId = null };
        using var createdResponse = await host.Client.PostAsJsonAsync("/api/rules", initial);
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = (await createdResponse.Content.ReadFromJsonAsync<TriggerRuleDto>())!;
        var route = $"/api/rules/{created.Id}";
        var before = await host.ReadStateAsync();
        foreach (var field in new[] { "name", "entityId" })
        {
            var body = JsonSerializer.SerializeToNode(initial with { ConfigurationVersion = created.ConfigurationVersion }, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
            body[field] = null;
            using var invalidCreate = await host.Client.PostAsJsonAsync("/api/rules", body);
            using var invalidUpdate = await host.Client.PutAsJsonAsync(route, body);
            Assert.Equal(HttpStatusCode.BadRequest, invalidCreate.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, invalidUpdate.StatusCode);
            Assert.Equal(before, await host.ReadStateAsync());
        }
        using (var invalidCreate = await host.Client.PostAsJsonAsync("/api/rules", initial with { EntityId = "unregistered-provider-id", Enabled = true }))
            Assert.Equal(HttpStatusCode.BadRequest, invalidCreate.StatusCode);
        using (var invalidUpdate = await host.Client.PutAsJsonAsync(route, initial with
        { EntityId = Guid.NewGuid().ToString("D"), Enabled = true, ConfigurationVersion = created.ConfigurationVersion }))
            Assert.Equal(HttpStatusCode.BadRequest, invalidUpdate.StatusCode);
        Assert.Equal(before, await host.ReadStateAsync());
        await using (var db = host.Factory.CreateDbContext())
        {
            (await db.IntegrationDeviceBindings.SingleAsync(device => device.Id == EditableSocket)).Enabled = false;
            await db.SaveChangesAsync();
        }
        using var invalidToggle = await host.Client.PatchAsJsonAsync(route + "/enabled", new RuleEnabledRequest(true, created.ConfigurationVersion));
        Assert.Equal(HttpStatusCode.BadRequest, invalidToggle.StatusCode);
        var saved = (await host.Client.GetFromJsonAsync<TriggerRuleDto>(route))!;
        Assert.False(saved.Enabled);
        Assert.Equal(created.ConfigurationVersion, saved.ConfigurationVersion);
        Assert.Equal(0, host.Socket.Calls);
    }

    [SqlServerFact]
    public async Task SalesAcceptsRealMobileBearerWithoutCookie()
    {
        await using var host = await ApiHost.StartAsync();
        var session = await host.LoginAsync();
        var before = await host.ReadStateAsync();
        using var response = await host.SendAsync(ProtectedRoutes[0], session.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0.5m, (await response.Content.ReadFromJsonAsync<ExportSalesResult>())!.EnergyValuePln);
        Assert.Equal(1, host.Sales.Calls);
        Assert.Equal(0, host.Source.Calls);
        Assert.Equal(before, await host.ReadStateAsync());
    }

    [SqlServerFact]
    public async Task RemovedSocketMutationEndpointCannotReachHardwareOrPersistence()
    {
        await using var host = await ApiHost.StartAsync();
        var session = await host.LoginAsync();
        var before = await host.ReadStateAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/devices/state")
        { Content = JsonContent.Create(new { entityId = "socket-neighbor", turnOn = true }) };
        request.Headers.Authorization = new("Bearer", session.Token);
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, host.Socket.Calls);
        Assert.Equal(before, await host.ReadStateAsync());
    }

    [SqlServerFact]
    public async Task AnonymousForgedExpiredAndRevokedTokensCannotReadOrRefresh()
    {
        await using var host = await ApiHost.StartAsync();
        using (var badLogin = await host.Client.PostAsJsonAsync("/api/auth/login", new MobileLoginRequest(Username, "incorrect")))
            Assert.Equal(HttpStatusCode.Unauthorized, badLogin.StatusCode);
        var valid = await host.LoginAsync();
        var expired = host.Sessions.Create("expired-fixture-user", "expired-fixture");
        var sessions = (ConcurrentDictionary<string, MobileSession>)typeof(MobileSessionStore)
            .GetField("_sessions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(host.Sessions)!;
        sessions[expired.Token] = expired with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var revoked = host.Sessions.Create("revoked-fixture-user", "revoked-fixture");
        host.Sessions.Revoke(revoked.Token);
        var state = await host.ReadStateAsync();
        var commands = host.Factory.DataCommands.Commands;

        foreach (var token in new string?[] { null, "forged-mobile-token", expired.Token, revoked.Token })
            foreach (var route in ProtectedRoutes)
            {
                using var response = await host.SendAsync(route, token);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }

        Assert.Equal(commands, host.Factory.DataCommands.Commands);
        Assert.Equal(0, host.Source.Calls);
        Assert.Equal(0, host.HistoryWeather.Calls);
        Assert.Equal(0, host.Sales.Calls);
        Assert.Equal(state, await host.ReadStateAsync());
        using var independentSession = await host.SendAsync("/api/solar/estimate", valid.Token);
        Assert.Equal(HttpStatusCode.OK, independentSession.StatusCode);
    }

    [SqlServerFact]
    public async Task RealMobileLoginAndCookieCanReadExistingModelsWithoutExposingCredentials()
    {
        await using var host = await ApiHost.StartAsync();
        var session = await host.LoginAsync();
        var before = await host.ReadStateAsync();
        using (var response = await host.SendAsync("/api/solar/estimate", session.Token))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("private-test-credential", json);
            var estimate = JsonSerializer.Deserialize<SolarEstimateState>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.NotNull(estimate.Estimate);
            Assert.Equal(SolarPowerBasis.PvDc, estimate.Estimate.Basis);
            Assert.True(estimate.Estimate.UpperKw > estimate.Estimate.LowerKw);
            Assert.False(estimate.RefreshFailed);
        }
        foreach (var period in new[] { "Today", "Week", "Month" })
        {
            using var response = await host.SendAsync("/api/solar/history?period=" + period + "&deviceSn=neighbor", session.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var history = (await response.Content.ReadFromJsonAsync<SolarHistoryResult>())!;
            Assert.Equal(new DateOnly(2026, 9, 30), history.SelectedDate);
            Assert.Equal(history.Today, history.SelectedDate);
            Assert.Equal("Europe/Warsaw", history.TimeZoneId);
            var measured = Assert.Single(history.Points, point => point.ActualKw.HasValue);
            Assert.Equal(2, measured.ActualKw);
            Assert.Equal(Now.Date.AddHours(11), measured.Timestamp.UtcDateTime);
            Assert.NotNull(measured.Possible);
        }
        using (var response = await host.SendAsync("/api/solar/history?period=Today&date=2026-09-29", session.Token))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(new DateOnly(2026, 9, 29), (await response.Content.ReadFromJsonAsync<SolarHistoryResult>())!.SelectedDate);
        }
        using (var response = await host.SendAsync(ProtectedRoutes[0], session.Token))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(0.5m, (await response.Content.ReadFromJsonAsync<ExportSalesResult>())!.EnergyValuePln);
        }
        Assert.Equal(before, await host.ReadStateAsync());

        // Real Identity cookie sign-in is confined to this loopback host, using the same seeded credentials.
        using (var login = await host.Client.PostAsJsonAsync("/test/cookie-login", new MobileLoginRequest(Username, Password)))
            Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        foreach (var route in ProtectedRoutes.Take(3))
        {
            using var response = await host.SendAsync(route, null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Equal(before, await host.ReadStateAsync());
    }

    [SqlServerFact]
    public async Task InvalidHistoryQueriesDoNotReachWeatherOrStorage()
    {
        await using var host = await ApiHost.StartAsync();
        var token = (await host.LoginAsync()).Token;
        var before = await host.ReadStateAsync();
        var commands = host.Factory.DataCommands.Commands;
        foreach (var query in new[] { "", "period=0", "period=3", "period=today", "period=Today,Week",
                     "period=Today&period=Week", "period=Today&date=", "period=Today&date=2026-9-30",
                     "period=Today&date=2026-09-31", "period=Today&date=2026-09-30&date=2026-09-29",
                     "period=Today&date=2026-10-01", "period=Today&date=2026-08-31" })
        {
            using var response = await host.SendAsync("/api/solar/history?" + query, token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Equal(commands, host.Factory.DataCommands.Commands);
        Assert.Equal(0, host.HistoryWeather.Calls);
        Assert.Equal(0, host.Source.Calls);
        Assert.Equal(before, await host.ReadStateAsync());
    }

    [SqlServerFact]
    public async Task RefreshUsesCanonicalAtomicWriterAndPreservesRulesSettingsAndNeighbor()
    {
        await using var host = await ApiHost.StartAsync();
        var token = (await host.LoginAsync()).Token;
        var before = await host.ReadStateAsync();
        host.Source.Fail = true;
        using (var failed = await host.SendAsync("/api/dashboard/refresh", token))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            var body = await failed.Content.ReadAsStringAsync();
            Assert.Contains("Could not refresh Deye readings", body);
            Assert.DoesNotContain("private-test-credential", body);
        }
        Assert.Equal(before, await host.ReadStateAsync());
        Assert.Equal(-100, host.Snapshot.Current!.GridConsumption);
        host.Source.Fail = false;
        using (var response = await host.SendAsync("/api/dashboard/refresh?deviceSn=neighbor", token))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var dashboard = (await response.Content.ReadFromJsonAsync<MobileDashboardResponse>())!;
            Assert.Equal(-2500, dashboard.Inverter!.GridConsumption);
            Assert.Equal(90, dashboard.Inverter.BatterySoc);
            Assert.Equal("Europe/Warsaw", dashboard.TimeZoneId);
            Assert.Single(dashboard.Rules);
            Assert.Single(dashboard.ManualDevices);
        }
        using (var response = await host.SendAsync("/api/dashboard", token))
            Assert.Equal(-2500, (await response.Content.ReadFromJsonAsync<MobileDashboardResponse>())!.Inverter!.GridConsumption);
        Assert.Equal(2, host.Source.Calls);
        await using var db = host.Factory.CreateDbContext();
        Assert.Equal(27, await db.Readings.CountAsync());
        Assert.Equal(-2500, (await db.ExportReadings.SingleAsync(row => row.DeviceSn == "selected")).GridPowerWatts);
        Assert.Equal(8765, (await db.ExportReadings.SingleAsync(row => row.DeviceSn == "neighbor")).GridPowerWatts);
        var rule = await db.TriggerRules.SingleAsync();
        Assert.False(rule.CurrentState);
        Assert.Null(rule.LastEvaluated);
        Assert.Empty(await db.RuleRunLogs.ToListAsync());
        Assert.Equal("private-test-credential", (await db.AppSettings.SingleAsync(row => row.Section == "PrivateFixture")).Value);
        Assert.Equal(0, host.Socket.Calls);
    }

    [SqlServerFact]
    public async Task CanceledHttpRefreshDoesNotSaveLateDataAndAllowsRetry()
    {
        await using var host = await ApiHost.StartAsync();
        var token = (await host.LoginAsync()).Token;
        var before = await host.ReadStateAsync();
        host.Source.Block = true;
        using var canceled = new CancellationTokenSource();
        var request = host.SendAsync("/api/dashboard/refresh", token, canceled.Token);
        await host.Source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await host.Source.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(before, await host.ReadStateAsync());
        Assert.Equal(-100, host.Snapshot.Current!.GridConsumption);
        host.Source.Block = false;
        using var retried = await host.SendAsync("/api/dashboard/refresh", token);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal(2, host.Source.Calls);
        Assert.Equal(0, host.Socket.Calls);
    }

    private sealed class ApiHost(WebApplication application, HttpClient client, Factory factory,
        Telemetry source, HistoryWeather weather, Sales sales, Socket socket) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public Factory Factory { get; } = factory;
        public Telemetry Source { get; } = source;
        public HistoryWeather HistoryWeather { get; } = weather;
        public Sales Sales { get; } = sales;
        public Socket Socket { get; } = socket;
        public MobileSessionStore Sessions => application.Services.GetRequiredService<MobileSessionStore>();
        public InverterDataSnapshot Snapshot => application.Services.GetRequiredService<InverterDataSnapshot>();
        public async Task<MobileAuthResponse> LoginAsync()
        {
            using var response = await Client.PostAsJsonAsync("/api/auth/login", new MobileLoginRequest(Username, Password));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var session = (await response.Content.ReadFromJsonAsync<MobileAuthResponse>())!;
            Assert.Equal(Username, session.Username);
            Assert.True(session.ExpiresAt > DateTimeOffset.UtcNow);
            Assert.Equal(64, session.Token.Length);
            return session;
        }
        public async Task<HttpResponseMessage> SendAsync(string path, string? token, CancellationToken ct = default)
        {
            using var request = new HttpRequestMessage(path.StartsWith("/api/dashboard/refresh", StringComparison.Ordinal)
                ? HttpMethod.Post : HttpMethod.Get, path);
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await Client.SendAsync(request, ct);
        }
        public async Task<string> ReadStateAsync()
        {
            await using var db = Factory.CreateDbContext();
            return JsonSerializer.Serialize(new
            {
                Readings = await db.Readings.AsNoTracking().OrderBy(row => row.Id).ToListAsync(),
                Exports = await db.ExportReadings.AsNoTracking().OrderBy(row => row.DeviceSn).ThenBy(row => row.ObservedAt).ToListAsync(),
                Rules = await db.TriggerRules.AsNoTracking().OrderBy(row => row.Id).ToListAsync(),
                Logs = await db.RuleRunLogs.AsNoTracking().OrderBy(row => row.Id).ToListAsync(),
                Settings = await db.AppSettings.AsNoTracking().OrderBy(row => row.Id).ToListAsync()
            });
        }
        public static async Task<ApiHost> StartAsync()
        {
            var connection = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SOLAR_TEST_SQL_CONNECTION"))
            { InitialCatalog = "SolarMobileApiTests_" + Guid.NewGuid().ToString("N") };
            var options = new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlServer(connection.ConnectionString).Options;
            var factory = new Factory(options);
            WebApplication? app = null;
            try
            {
                await using (var db = factory.CreateDbContext())
                {
                    await db.Database.MigrateAsync();
                    db.Installations.Add(new Installation { Id = FixtureInstallation, CreatedAt = DateTimeOffset.UtcNow });
                    var instance = new IntegrationInstanceEntity { Id = Guid.NewGuid(), ProviderId = "socket.fixture", State = "enabled" };
                    db.IntegrationInstances.Add(instance);
                    db.IntegrationDeviceBindings.Add(new() { Id = EditableSocket, InstanceId = instance.Id, Kind = "socket", RemoteId = "editable-socket",
                        MetadataJson = "{\"capabilities\":{\"canSwitch\":true}}" });
                    for (var minute = 0; minute <= 60; minute += 5)
                    {
                        var observed = Now.Date.AddHours(11).AddMinutes(minute);
                        db.Readings.AddRange(
                            new Reading { Timestamp = Now.UtcDateTime, SolarObservedAt = observed, SolarDeviceSn = "selected", SolarProduction = 2000 },
                            new Reading { Timestamp = Now.UtcDateTime, SolarObservedAt = observed, SolarDeviceSn = "neighbor", SolarProduction = 9000 });
                    }
                    db.ExportReadings.Add(new() { DeviceSn = "neighbor", ObservedAt = Now.AddMinutes(-1).UtcDateTime, PolledAt = Now.UtcDateTime, GridPowerWatts = 8765 });
                    db.TriggerRules.Add(new() { Name = "independent rule", EntityId = EditableSocket.ToString("D"), SocTurnOnThreshold = 50 });
                    db.AppSettings.AddRange(new AppSetting { Section = "Display", Key = "TimeZoneId", Value = "Europe/Warsaw" },
                        new AppSetting { Section = "PrivateFixture", Key = "Secret", Value = "private-test-credential" });
                    await db.SaveChangesAsync();
                }
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = AppContext.BaseDirectory });
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Logging.ClearProviders();
                builder.Services.AddSingleton<IDbContextFactory<DeyeSolarDbContext>>(factory);
                builder.Services.AddScoped(_ => factory.CreateDbContext());
                builder.Services.AddIdentity<IdentityUser, IdentityRole>().AddEntityFrameworkStores<DeyeSolarDbContext>();
                builder.Services.AddAccountIdentities(new AuthProviderOptions());
                builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, MobileBearerAuthenticationHandler>(MobileBearerAuthenticationHandler.SchemeName, _ => { });
                builder.Services.AddAuthorization();
                builder.Services.AddSingleton<MobileSessionStore>();
            builder.Services.AddSingleton<DeyeSolar.Web.Auth.IAccountSessionStore>(p => p.GetRequiredService<MobileSessionStore>());
                builder.Services.AddScoped<MobileAuthService>();
                builder.Services.AddSingleton<TimeProvider>(new Clock());
                builder.Services.AddSingleton<IOptionsMonitor<InverterConnectionOptions>>(new Monitor<InverterConnectionOptions>(new() { DeviceKey = "selected" }));
                builder.Services.AddSingleton<IOptionsMonitor<SolarEstimateOptions>>(new Monitor<SolarEstimateOptions>(new()
                { DeyeSolarPowerIsPvDcConfirmed = true, DeyeConfirmedDeviceSn = "selected" }));
                var source = new Telemetry(); var weather = new HistoryWeather(); var sales = new Sales(); var socket = new Socket();
                builder.Services.AddSingleton<IInverterDataSource>(source);
                builder.Services.AddSingleton<ISolarHistoryRadiationSource>(weather);
                builder.Services.AddSingleton<IExportSalesService>(sales);
                builder.Services.AddSingleton<ISocketController>(socket);
                builder.Services.AddSingleton<ISocketInventoryService>(socket);
                builder.Services.AddSingleton<InverterDataSnapshot>();
                builder.Services.AddSingleton<DeviceStatusSnapshot>();
                builder.Services.AddSingleton<IConfigurationRules, RuleRepository>();
                builder.Services.AddSingleton<AppSettingsService>();
                builder.Services.AddSingleton<IAppSettingsReader>(provider => provider.GetRequiredService<AppSettingsService>());
                builder.Services.AddSingleton<IAppSettingsWriter>(provider => provider.GetRequiredService<AppSettingsService>());
                builder.Services.AddSingleton<ExportReadingStore>();
                builder.Services.AddSingleton<IInverterRefreshService, InverterRefreshService>();
                builder.Services.AddSingleton<ISolarHistoryStore, SolarHistoryStore>();
                builder.Services.AddSingleton<ISolarHistoryService, SolarHistoryService>();
                builder.Services.AddSingleton<ISolarRadiationSource, CurrentWeather>();
                builder.Services.AddSingleton<ISolarEstimateStore, SolarEstimateStore>();
                builder.Services.AddSingleton<SolarEstimateService>();
                app = builder.Build();
                app.UseRouting(); app.UseRateLimiter(); app.UseAuthentication(); app.UseAuthorization();
                app.MapMobileApi(); app.MapExportSalesApi();
                app.MapPost("/test/cookie-login", async (MobileLoginRequest request, SignInManager<IdentityUser> signIn) =>
                    (await signIn.PasswordSignInAsync(request.Username, request.Password, false, false)).Succeeded
                        ? Results.NoContent() : Results.Unauthorized());
                using (var scope = app.Services.CreateScope())
                {
                    var user = new IdentityUser { UserName = Username, Email = "mobile-reader@example.test", EmailConfirmed = true };
                    var result = await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().CreateAsync(user, Password);
                    Assert.True(result.Succeeded, string.Join(",", result.Errors.Select(error => error.Code)));
                    var db = scope.ServiceProvider.GetRequiredService<DeyeSolarDbContext>();
                    db.InstallationMemberships.Add(new InstallationMembership { UserId = user.Id, InstallationId = FixtureInstallation });
                    await db.SaveChangesAsync();
                }
                app.Services.GetRequiredService<InverterDataSnapshot>().Update(new() { Timestamp = Now.AddMinutes(-5), GridConsumption = -100 });
                app.Services.GetRequiredService<DeviceStatusSnapshot>().Update([new(EditableSocket.ToString("D"), "Neighbor socket", null, true, false, 0)]);
                await app.Services.GetRequiredService<SolarEstimateService>().UpdateAsync(default);
                await app.StartAsync();
                var address = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses));
                Assert.Equal("127.0.0.1", address.Host);
                var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(15) };
                return new(app, client, factory, source, weather, sales, socket);
            }
            catch
            {
                if (app is not null) await app.DisposeAsync();
                await using var db = factory.CreateDbContext();
                await db.Database.EnsureDeletedAsync();
                throw;
            }
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            try { await application.StopAsync(); await application.DisposeAsync(); }
            finally { await using var db = Factory.CreateDbContext(); await db.Database.EnsureDeletedAsync(); }
        }
    }
    private sealed class Factory(DbContextOptions<DeyeSolarDbContext> options) : IDbContextFactory<DeyeSolarDbContext>
    {
        public SolarDataCommandCounter DataCommands { get; } = new();
        public DeyeSolarDbContext CreateDbContext() => new(new DbContextOptionsBuilder<DeyeSolarDbContext>(options)
            .AddInterceptors(DataCommands).Options, FixtureInstallation);
        public Task<DeyeSolarDbContext> CreateDbContextAsync(CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(CreateDbContext()); }
    }
    private sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    { public T CurrentValue => value; public T Get(string? name) => value; public IDisposable? OnChange(Action<T, string?> listener) => null; }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Telemetry : IInverterDataSource
    {
        public int Calls; public bool Fail; public bool Block;
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<InverterData> ReadCurrentDataAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Fail) throw new InvalidDataException("private-test-credential");
            if (Block)
            {
                Started.TrySetResult(true);
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { Canceled.TrySetResult(true); throw; }
            }
            return new() { Timestamp = Now, BatterySoc = 90, GridConsumption = -2500, GridDeviceSn = "selected",
                GridObservedAt = Now.AddMinutes(-1), SolarProduction = 3100, SolarObservedAt = Now.AddMinutes(-1), SolarDeviceSn = "selected" };
        }
    }
    private sealed class HistoryWeather : ISolarHistoryRadiationSource
    {
        public int Calls;
        public Task<IReadOnlyList<SolarWeatherSample>> ReadAsync(SolarEstimateOptions options, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<SolarWeatherSample>>(Enumerable.Range(0, (int)(end - start).TotalHours)
                .Select(index => new SolarWeatherSample(start.AddHours(index), 700, 300, 20, 2, 30)).ToArray());
        }
    }
    private sealed class CurrentWeather : ISolarRadiationSource
    {
        public Task<SolarRadiationObservation> ReadAsync(SolarEstimateOptions options, DateTimeOffset now, CancellationToken ct)
            => Task.FromResult(new SolarRadiationObservation(now, 700, 300, 20, 2, now, 0)
            { Kind = SolarRadiationKind.WeatherModel, RetrievedAt = now, Forecast = Enumerable.Range(-1, 3)
                .Select(index => new SolarWeatherSample(now.AddMinutes(index * 15), 700, 300, 20, 2, 30)).ToArray() });
    }
    private sealed class Sales : IExportSalesService
    {
        public int Calls;
        public Task<ExportSalesResult> ReadAsync(ExportSalesRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new ExportSalesResult(request, new(2026, 9, 30), new(2026, 9, 28), "Europe/Warsaw",
                Now.AddHours(-1), Now, [], 1, 1, .5m, .615m, 1, 1, 1));
        }
    }
    private sealed class Socket : ISocketController, ISocketInventoryService
    {
        public int Calls;
        public Task TurnOnAsync(string entityId, CancellationToken ct) { Calls++; throw new InvalidOperationException("No rule or socket side effects allowed."); }
        public Task TurnOffAsync(string entityId, CancellationToken ct) => TurnOnAsync(entityId, ct);
        public Task<bool> GetStateAsync(string entityId, CancellationToken ct) => Task.FromResult(false);
        public Task<IReadOnlyList<DevicePowerInfo>> GetCachedDevicesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DevicePowerInfo>>([]);
        public Task<IReadOnlyList<DevicePowerInfo>> RefreshDevicesAsync(CancellationToken ct) => GetCachedDevicesAsync(ct);
    }
}
