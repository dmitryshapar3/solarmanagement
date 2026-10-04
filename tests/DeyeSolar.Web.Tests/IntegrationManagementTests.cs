using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using SolarManagement.Inverters.Contracts;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using DeyeSolar.Web.Api;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Web.Tests;

public class IntegrationManagementTests
{
    private const string SocketId = "5748365a-4d79-4cf2-9cba-663df1e37c5a";
    [Theory]
    [InlineData("deye")]
    [InlineData("shelly")]
    public async Task UnsupportedProbeKindsCannotInvokeAProviderOrLoadCredentials(string provider)
    {
        var fixture = Probe();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.TestAsync(provider, new(), CancellationToken.None));
        Assert.Equal(0, fixture.Transport.Calls);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.1.1")]
    [InlineData("192.168.10.5")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.1.2")]
    [InlineData("::1")]
    [InlineData("::ffff:192.168.0.1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    [InlineData("192.0.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("192.88.99.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("2001::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2002:7f00:1::1")]
    [InlineData("3fff::1")]
    public void PrivateAddressesCannotBeProviderConnections(string ip) => Assert.False(ProviderEndpointPolicy.IsPublicAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2001:4860:4860::8888")]
    public void NormalPublicProviderAddressesAreAllowed(string ip) => Assert.True(ProviderEndpointPolicy.IsPublicAddress(IPAddress.Parse(ip)));

    [Fact]
    public async Task FailedProviderBodyAndRedirectNeverAppearInResultsOrCauseASecondRequest()
    {
        var fixture = Probe((request, _) => new(HttpStatusCode.Redirect)
        {
            RequestMessage = request,
            Content = new StringContent("private-password private-cloud-key"),
            Headers = { Location = new("https://evil.example") }
        });
        var result = await fixture.Service.TestAsync("openmeteo", new(), CancellationToken.None);
        Assert.False(result.Success); Assert.Equal("unavailable", result.Code); Assert.Equal(1, fixture.Transport.Calls);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task WeatherOnlyUsesFixedProviderAndServerKeyWithDraftCoordinates()
    {
        var fixture = Probe((request, _) => Json(request, "{\"current\":{\"temperature_2m\":21}}"), "private-weather-key");
        var result = await fixture.Service.TestAsync("openmeteo", new(SolarEstimate: new(12.5, 23.75)), CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal("customer-api.open-meteo.com", fixture.Transport.Uris.Single().Host);
        Assert.Contains("latitude=12.5", fixture.Transport.Uris.Single().Query);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task RepeatedProviderTestsAreBoundedAndDoNotCallTheProviderDuringCooldown()
    {
        var fixture = Probe((request, _) => Json(request, "{\"value\":[]}"));
        Assert.True((await fixture.Service.TestAsync("pse", new(), CancellationToken.None)).Success);
        Assert.Equal("busy", (await fixture.Service.TestAsync("pse", new(), CancellationToken.None)).Code);
        Assert.Equal(1, fixture.Transport.Calls);
    }

    [Fact]
    public async Task LocalRenamePersistsAcrossRequestsPreservesCloudIdentityAndResetRestoresIt()
    {
        var store = new Labels(); var snapshot = Devices();
        var first = new DeviceNameService(store, snapshot);
        var renamed = await first.RenameAsync(SocketId.ToUpperInvariant(), "  Garden socket  ", CancellationToken.None);
        Assert.Equal("Garden socket", renamed!.Name); Assert.Equal("Cloud socket", renamed.CloudName); Assert.Equal(SocketId, renamed.Id);
        var inventory = snapshot.Current; Assert.NotNull(inventory);
        Assert.Equal("Cloud socket", inventory.Single().Name); Assert.False(inventory.Single().IsOn);
        var next = new DeviceNameService(store, snapshot);
        Assert.Equal("Garden socket", (await next.DescribeAsync(inventory, CancellationToken.None)).Single().LocalName);
        var reset = await next.RenameAsync(SocketId, null, CancellationToken.None);
        Assert.Equal("Cloud socket", reset!.Name); Assert.Null(reset.LocalName);
    }

    [Fact]
    public async Task UnknownDevicesAndInvalidNamesCannotCreateLabelsOrOperateHardware()
    {
        var store = new Labels(); var service = new DeviceNameService(store, Devices());
        Assert.Null(await service.RenameAsync("neighbour-device", "Forbidden", CancellationToken.None));
        Assert.Equal(0, store.Saves);
        await Assert.ThrowsAsync<ArgumentException>(() => service.RenameAsync(SocketId, "name\nwith-control", CancellationToken.None));
        Assert.False(DeviceNameService.TryName(new string('x', 81), out _));
        Assert.Equal(0, store.Saves);
    }

    [Theory]
    [InlineData("abcdef")]
    [InlineData("shelly:abcdef")]
    [InlineData("shelly:" + SocketId)]
    [InlineData("socket:" + SocketId)]
    public async Task RawAndPrefixedDeviceIdsCannotReadOrCreateLabelsEvenWhenDiscovered(string id)
    {
        var store = new Labels();
        var snapshot = Devices();
        snapshot.Update(snapshot.Current!.Append(new DevicePowerInfo(id, "Invalid fixture device", "Socket", true, false, 0)).ToArray());
        var service = new DeviceNameService(store, snapshot);

        Assert.Null(await service.RenameAsync(id, "Forbidden", CancellationToken.None));
        Assert.Equal(0, store.Loads);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public void ManualDevicesUseTheCurrentGuidIdentityAsAnUnnamedFallback()
    {
        var device = Assert.Single(ManualSocketDevices.Build([new TriggerRule { EntityId = SocketId }]));
        Assert.Equal(SocketId, device.Id);
        Assert.Equal(SocketId, device.Name);
        Assert.Equal("Socket", device.Category);
    }

    [Theory]
    [InlineData("abcdef")]
    [InlineData("shelly:abcdef")]
    [InlineData("shelly:" + SocketId)]
    public async Task InvalidDisabledDraftsDoNotBecomeManualDevicesAndRetiredGuidDraftsRemainReadable(string id)
    {
        var devices = ManualSocketDevices.Build([
            new TriggerRule { EntityId = id, Enabled = false },
            new TriggerRule { EntityId = SocketId, Enabled = false }
        ]);
        Assert.Equal(SocketId, Assert.Single(devices).Id);
        var described = await new DeviceNameService(new Labels(), new DeviceStatusSnapshot()).DescribeAsync(devices, CancellationToken.None);
        Assert.Equal(SocketId, Assert.Single(described).Id);
    }

    [Fact]
    public void SiteConfigurationAllowsOneUnusedPanelGroupAndRejectsInvalidCoordinatesOrNoCapacity()
    {
        var site = Site();
        Assert.True(SiteSettingsService.TryValidate(site, out _));
        Assert.False(SiteSettingsService.TryValidate(site with { SolarEstimate = site.SolarEstimate with { Roof1Kwp = 0 } }, out _));
        Assert.False(SiteSettingsService.TryValidate(site with { SolarEstimate = site.SolarEstimate with { Latitude = double.NaN } }, out _));
        Assert.False(SiteSettingsService.TryValidate(site with { SolarSales = site.SolarSales with { ContractStartDate = "30/09/2026" } }, out _));
        Assert.DoesNotContain("ApiKey", JsonSerializer.Serialize(site));
    }

    [Fact]
    public async Task PvConfirmationIsBoundToSavedInverterAndSiteSavePreservesServerOnlySettings()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        connection.CreateCollation("Latin1_General_100_BIN2", string.CompareOrdinal);
        var options = new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlite(connection).Options;
        var factory = new SiteFactory(options);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            db.Installations.Add(new Installation { Id = "fictional-site", CreatedAt = DateTimeOffset.UtcNow });
            db.AppSettings.AddRange(new AppSetting { Section = "SolarEstimate", Key = "ApiKey", Value = "server-only-secret" },
                new AppSetting { Section = "SolarEstimate", Key = "InverterEfficiency", Value = "0.91" });
            await db.SaveChangesAsync();
        }
        var settings = new AppSettingsService(factory, new ConfigurationBuilder().Build());
        var selected = new InverterConnectionOptions { DeviceKey = Guid.NewGuid().ToString("D") };
        var service = new SiteSettingsService(settings, settings, new Monitor<InverterConnectionOptions>(selected), new NoInverterSource());
        var wrong = Site() with
        {
            SelectedDeviceSn = "forged-inverter",
            SolarEstimate = Site().SolarEstimate with
            { DeyeSolarPowerIsPvDcConfirmed = true, DeyeSolarPowerConfirmedDeviceSn = "forged-inverter" }
        };
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(wrong));
        var matching = wrong with { SolarEstimate = wrong.SolarEstimate with { DeyeSolarPowerConfirmedDeviceSn = selected.DeviceKey } };
        await service.SaveAsync(matching);
        var loaded = await service.LoadAsync();
        Assert.Equal(selected.DeviceKey, loaded.SelectedDeviceSn); Assert.True(loaded.SolarEstimate.DeyeSolarPowerIsPvDcConfirmed);
        Assert.Equal(selected.DeviceKey, loaded.SolarEstimate.DeyeSolarPowerConfirmedDeviceSn);
        Assert.DoesNotContain("server-only-secret", JsonSerializer.Serialize(loaded));
        selected.DeviceKey = Guid.NewGuid().ToString("D");
        var changed = await service.LoadAsync(); Assert.False(changed.SolarEstimate.DeyeSolarPowerIsPvDcConfirmed);
        Assert.Equal("", changed.SolarEstimate.DeyeSolarPowerConfirmedDeviceSn);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(matching));
        await service.SaveAsync(matching with { SolarEstimate = matching.SolarEstimate with { DeyeSolarPowerIsPvDcConfirmed = false } });
        await using var check = factory.CreateDbContext();
        var rows = await check.AppSettings.Where(row => row.Section == "SolarEstimate").ToDictionaryAsync(row => row.Key, row => row.Value);
        Assert.Equal("", rows["DeyeConfirmedDeviceSn"]); Assert.Equal("False", rows["DeyeSolarPowerIsPvDcConfirmed"]);
        Assert.Equal("server-only-secret", rows["ApiKey"]); Assert.Equal("0.91", rows["InverterEfficiency"]);
    }

    [Fact]
    public async Task ProbeEndpointRequiresAuthAndCookieVerificationWhileNativeBearerWorks()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.Sources.Clear(); builder.Configuration.AddInMemoryCollection(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, FixtureAuth>(IdentityConstants.ApplicationScheme, _ => { })
            .AddScheme<AuthenticationSchemeOptions, FixtureAuth>(MobileBearerAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization(); builder.Services.AddAntiforgery();
        var tests = new CountingTests(); builder.Services.AddSingleton<IIntegrationTestService>(tests);
        builder.Services.AddSingleton(new DeviceNameService(new Labels(), Devices()));
        var settings = new AppSettingsService(new NeverFactory(), new ConfigurationBuilder().Build());
        builder.Services.AddSingleton(new SiteSettingsService(settings, settings,
            new Monitor<InverterConnectionOptions>(new()), new NoInverterSource()));
        await using var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization(); app.MapIntegrationManagement();
        app.MapGet("/_fixture/csrf", (HttpContext context, IAntiforgery antiforgery) =>
            Results.Ok(new { requestToken = antiforgery.GetAndStoreTokens(context).RequestToken }))
            .RequireAuthorization(ApiAuthorization.AuthenticatedUser);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new(address) };
        using (var anonymous = await client.PostAsJsonAsync("/api/settings/test/pse", new IntegrationTestRequest())) Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Add("X-Fixture-Cookie", "yes");
        using (var cookie = await client.PostAsJsonAsync("/api/settings/test/pse", new IntegrationTestRequest())) Assert.Equal(HttpStatusCode.BadRequest, cookie.StatusCode);
        Assert.Equal(0, tests.Calls);
        var verification = await client.GetFromJsonAsync<JsonElement>("/_fixture/csrf");
        client.DefaultRequestHeaders.Add("RequestVerificationToken", verification.GetProperty("requestToken").GetString());
        using (var cookieVerified = await client.PostAsJsonAsync("/api/settings/test/pse", new IntegrationTestRequest())) Assert.Equal(HttpStatusCode.OK, cookieVerified.StatusCode);
        Assert.Equal(1, tests.Calls);
        client.DefaultRequestHeaders.Remove("RequestVerificationToken"); client.DefaultRequestHeaders.Authorization = new("Bearer", "fixture-valid");
        using (var mixed = await client.PostAsJsonAsync("/api/settings/test/pse", new IntegrationTestRequest())) Assert.Equal(HttpStatusCode.BadRequest, mixed.StatusCode);
        Assert.Equal(1, tests.Calls);
        client.DefaultRequestHeaders.Remove("X-Fixture-Cookie"); client.DefaultRequestHeaders.Authorization = new("Bearer", "fixture-valid");
        using (var bearer = await client.PostAsJsonAsync("/api/settings/test/pse", new IntegrationTestRequest())) Assert.Equal(HttpStatusCode.OK, bearer.StatusCode);
        Assert.Equal(2, tests.Calls);
        foreach (var removed in new[] { "deye", "shelly" })
            using (var removedProbe = await client.PostAsJsonAsync("/api/settings/test/" + removed, new IntegrationTestRequest()))
                Assert.Equal(HttpStatusCode.NotFound, removedProbe.StatusCode);
        Assert.Equal(2, tests.Calls);
        using (var alias = await client.PatchAsJsonAsync("/api/devices/foreign/name", new DeviceNameRequest("Forbidden"))) Assert.Equal(HttpStatusCode.NotFound, alias.StatusCode);
    }

    private static SiteSettingsDto Site() => new(new(0, 0, "Fictional site", "UTC", 5, 0, 20, 0, 180, 0), new("2026-10-01", "UTC", false));
    private static DeviceStatusSnapshot Devices() { var value = new DeviceStatusSnapshot(); value.Update([new(SocketId, "Cloud socket", "Socket", true, false, 0)]); return value; }
    private static HttpResponseMessage Json(HttpRequestMessage request, string json) => new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(json) };
    private static (IntegrationTestService Service, Transport Transport) Probe(Func<HttpRequestMessage, int, HttpResponseMessage>? response = null, string? key = null)
    {
        var transport = new Transport(response ?? ((request, _) => Json(request, "{}")));
        return (new(new Clients(transport), new Monitor<SolarEstimateOptions>(new() { ApiKey = key }), TimeProvider.System, new()), transport);
    }
    private sealed class NeverFactory : IDbContextFactory<DeyeSolarDbContext> { public int Calls; public DeyeSolarDbContext CreateDbContext() { Calls++; throw new InvalidOperationException("Draft probes must not access storage."); } }
    private sealed class SiteFactory(DbContextOptions<DeyeSolarDbContext> options) : IDbContextFactory<DeyeSolarDbContext>
    { public DeyeSolarDbContext CreateDbContext() => new(options, "fictional-site"); }
    private sealed class Clients(Transport transport) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(transport, disposeHandler: false); }
    private sealed class Transport(Func<HttpRequestMessage, int, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls; public List<string> Paths = []; public List<string> Bodies = []; public List<Uri> Uris = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Calls++; Uris.Add(request.RequestUri!); Paths.Add(request.RequestUri!.AbsolutePath); Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)); return response(request, Calls); }
    }
    private sealed class Monitor<T>(T value) : IOptionsMonitor<T> { public T CurrentValue => value; public T Get(string? name) => value; public IDisposable? OnChange(Action<T, string?> listener) => null; }
    private sealed class NoInverterSource : IInverterDataSource
    {
        public Task<InverterData> ReadCurrentDataAsync(CancellationToken ct) => throw new InvalidOperationException("No inverter reads are allowed in site settings tests.");
    }
    private sealed class Labels : IDeviceLabelStore
    {
        public int Loads; public int Saves; private Dictionary<string, string> _labels = [];
        public Task<Dictionary<string, string>> LoadAsync(CancellationToken ct) { Loads++; return Task.FromResult(new Dictionary<string, string>(_labels)); }
        public Task SaveAsync(Dictionary<string, string> labels, CancellationToken ct) { Saves++; _labels = new(labels); return Task.CompletedTask; }
    }
    private sealed class CountingTests : IIntegrationTestService { public int Calls; public Task<IntegrationTestResult> TestAsync(string kind, IntegrationTestRequest request, CancellationToken ct) { Calls++; return Task.FromResult(new IntegrationTestResult(kind, true, "ok", "Fictional provider verified.", DateTimeOffset.UtcNow)); } }
    private sealed class FixtureAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var valid = Scheme.Name == IdentityConstants.ApplicationScheme ? Request.Headers.ContainsKey("X-Fixture-Cookie") : Request.Headers.Authorization == "Bearer fixture-valid";
            return Task.FromResult(!valid ? AuthenticateResult.NoResult() : AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "fixture-owner"), new Claim(InstallationIds.ClaimType, "fixture-installation")], Scheme.Name)), Scheme.Name)));
        }
    }
}
