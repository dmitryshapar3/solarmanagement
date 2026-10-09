using System.Net;
using DeyeSolar.Web.Components.Ui;
using DeyeSolar.Web.Localization;
using DeyeSolar.Web.Redesign;
using DeyeSolar.Web.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace DeyeSolar.Web.Tests;

public sealed class FeedbackNavigationTests
{
    [Theory]
    [InlineData("/activity", "/activity", "/activity/readings")]
    [InlineData("/activity/readings", "/activity/readings", "/activity")]
    public async Task ActivitySubpagesHaveDistinctSelectedLinksInSidebarAndPage(string current, string selected, string other)
    {
        var tabs = await RenderAsync<ActivityTabs>(current);
        var navigation = await RenderAsync<NavMenu>(current);
        Assert.Contains($"href=\"{selected}\" class=\"active\" aria-current=\"page\"", tabs);
        Assert.DoesNotContain($"href=\"{other}\" class=\"active\"", tabs);
        Assert.Contains("app-nav-submenu", navigation);
        Assert.Contains($"href=\"{selected}\" class=\"active\" aria-current=\"page\"", navigation);
        Assert.DoesNotContain($"href=\"{other}\" class=\"active\"", navigation);
        Assert.Contains(">Readings</a>", tabs);
        Assert.Contains(">Automation</a>", tabs);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConnectedServiceActionsRespectTheExistingPermission(bool canManage)
    {
        var html = await RenderAsync<ConnectedServiceCard>("/settings/connections", new()
        {
            ["Service"] = Service(), ["CanManage"] = canManage
        });
        Assert.Contains("Fixture inverter", html);
        Assert.Contains("Primary inverter", html);
        foreach (var action in new[] { "Check connection", "Find devices", "Connection settings" })
            Assert.Equal(canManage, html.Contains(action, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RussianConnectedServiceActionsStayInsideTheirCardsAtEveryViewport()
    {
        var root = RepositoryRoot();
        var card = await RenderAsync<ConnectedServiceCard>("/settings/connections", new()
        {
            ["Service"] = Service(), ["CanManage"] = true
        }, "ru");
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("https://feedback.test/**", async route =>
        {
            var path = new Uri(route.Request.Url).AbsolutePath.TrimStart('/');
            var file = Path.GetFullPath(Path.Combine(root, "src/DeyeSolar.Web/wwwroot", path));
            Assert.StartsWith(Path.Combine(root, "src/DeyeSolar.Web/wwwroot") + Path.DirectorySeparatorChar, file);
            await route.FulfillAsync(new() { Path = file });
        });
        foreach (var theme in new[] { "light", "dark" })
        foreach (var width in new[] { 320, 390, 1024, 1440 })
        {
            await page.SetViewportSizeAsync(width, 1000);
            await page.SetContentAsync($"<!doctype html><html lang='ru' data-theme='{theme}'><meta charset='utf-8'><link rel='stylesheet' href='https://feedback.test/css/fonts.css'><link rel='stylesheet' href='https://feedback.test/css/tokens.css'><link rel='stylesheet' href='https://feedback.test/css/smartsolar.css'><body><div class='app-shell'><aside class='sidebar'></aside><main class='app-content'><div class='two-columns connected-services'>{card}{card}</div></main></div></body></html>");
            await page.EvaluateAsync("document.fonts.ready");
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"), $"Page overflow at {width}px / {theme}");
            foreach (var element in await page.Locator(".service-actions .ui-button").AllAsync())
            {
                Assert.True(await element.EvaluateAsync<bool>("element => { const b=element.getBoundingClientRect(), c=element.closest('.ui-card').getBoundingClientRect(); return b.left>=c.left && b.right<=c.right && b.top>=c.top && b.bottom<=c.bottom && element.scrollWidth<=element.clientWidth; }"), $"Action outside card at {width}px / {theme}");
                Assert.True(await element.IsVisibleAsync());
            }
            if (Environment.GetEnvironmentVariable("SOLAR_FEEDBACK_QA_DIRECTORY") is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
                await page.ScreenshotAsync(new() { Path = Path.Combine(folder, $"connections-{width}-{theme}.png"), FullPage = true });
            }
        }
    }

    private static ConnectedServiceDto Service() => new(Guid.Parse("00000000-0000-0000-0000-000000000001"), "fixture", "Fixture inverter", "inverter", "connected", true, 0, "Roof inverter", DateTimeOffset.UtcNow, null);

    private static async Task<string> RenderAsync<T>(string route, Dictionary<string, object?>? parameters = null, string language = "en") where T : IComponent
    {
        var collection = new ServiceCollection();
        collection.AddLogging(); collection.AddComponentLocalization();
        collection.AddSingleton<NavigationManager>(new Navigation(route));
        await using var services = collection.BuildServiceProvider();
        services.GetRequiredService<UiText>().InitializeCircuit(language, language);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => WebUtility.HtmlDecode((await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters ?? []))).ToHtmlString()));
    }
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DeyeSolar.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository not found");
    }
    private sealed class Navigation : NavigationManager
    {
        public Navigation(string route) => Initialize("https://feedback.test/", "https://feedback.test" + route);
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
}
