using System.Net;
using System.Security.Claims;
using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Integrations;
using DeyeSolar.Web.Redesign;
using DeyeSolar.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace DeyeSolar.Web.Tests;

public sealed class SettingsRoofPreviewTests
{
    [Fact]
    public async Task InstallationPagePassesItsStoredSiteTimeZoneToTheRoofPreview()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        connection.CreateCollation("Latin1_General_100_BIN2", string.CompareOrdinal);
        var factory = new Factory(new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlite(connection).Options);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync(); await TestInstallation.EnsureAsync(db);
            db.AppSettings.AddRange(
                new AppSetting { Section = "SolarEstimate", Key = "TimeZoneId", Value = "Australia/Sydney" },
                new AppSetting { Section = "SolarEstimate", Key = "Latitude", Value = "-33.87" },
                new AppSetting { Section = "SolarEstimate", Key = "Longitude", Value = "151.2" },
                new AppSetting { Section = "SolarEstimate", Key = "Roof1Kwp", Value = "4.5" },
                new AppSetting { Section = "SolarEstimate", Key = "Roof2Kwp", Value = "0" });
            await db.SaveChangesAsync();
        }
        var current = new CurrentInstallation(); current.BindOnce(TestInstallation.Id);
        var security = new InteractiveSecurityContext(new Owner(), current, new HttpContextAccessor());
        security.BindOnce(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "roof-preview-owner")], "PreviewTest")));
        var collection = new ServiceCollection(); collection.AddLogging(); collection.AddComponentLocalization();
        collection.AddSingleton<IJSRuntime, NoJs>(); collection.AddSingleton<NavigationManager, Navigation>();
        collection.AddSingleton(security); collection.AddSingleton<IIntegrationTestService, NoProbe>();
        collection.AddSingleton<IDbContextFactory<DeyeSolarDbContext>>(factory);
        collection.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build()); collection.AddSingleton<AppSettingsService>();
        collection.AddSingleton<IntegrationChangeNotifier>(); collection.AddSingleton<InstallationSettingsService>();
        await using var services = collection.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => WebUtility.HtmlDecode(
            (await renderer.RenderComponentAsync<DeyeSolar.Web.Pages.Settings>(ParameterView.Empty)).ToHtmlString()));
        Assert.Contains("roof-sun-compass roof-scene-fallback", html);
        Assert.Contains("roof-scene-sun-path roof-sun-path", html);
        Assert.Contains("data-roof=\"1\"", html);
        Assert.DoesNotContain("data-roof=\"2\"", html);
        Assert.Contains("Australia/Sydney", html);
        Assert.DoesNotContain("Enter valid coordinates and a solar time zone to see the sun path.", html);
    }

    private sealed class Factory(DbContextOptions<DeyeSolarDbContext> options) : IDbContextFactory<DeyeSolarDbContext>
    { public DeyeSolarDbContext CreateDbContext() => new(options, TestInstallation.Id); }
    private sealed class Owner : IInstallationAccessAuthorizer
    {
        public Task<InstallationMembership> CheckAsync(ClaimsPrincipal actor, string installationId, InstallationPermission permission, CancellationToken ct = default)
            => Task.FromResult(new InstallationMembership { InstallationId = installationId, UserId = "roof-preview-owner", Role = "Owner" });
    }
    private sealed class NoProbe : IIntegrationTestService
    { public Task<IntegrationTestResult> TestAsync(string kind, IntegrationTestRequest request, CancellationToken ct) => throw new InvalidOperationException("Rendering must not contact a weather provider."); }
    private sealed class Navigation : NavigationManager
    {
        public Navigation() => Initialize("http://localhost/", "http://localhost/settings");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
        protected override void SetNavigationLockState(bool value) { }
    }
    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken ct, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
