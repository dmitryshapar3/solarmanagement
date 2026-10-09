using Microsoft.Playwright;

namespace DeyeSolar.Web.Tests;

public sealed class SettingsFeedbackBrowserTests
{
    [SqlServerFact]
    public async Task RoofPreviewRespondsToDraftChangesAndFixedExportPriceSurvivesReload()
    {
        await using var app = await BrowserBillingTests.ProductionApp.StartAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { Locale = "en-US", ViewportSize = new() { Width = 390, Height = 844 } });
        await context.RouteAsync("**/*", route => route.Request.Url.StartsWith(app.Address, StringComparison.Ordinal) ? route.ContinueAsync() : route.AbortAsync());
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        await page.GotoAsync(app.Address + "/signin?mode=password");
        await page.GetByLabel("Username, email or phone", new() { Exact = true }).FillAsync("billing-browser@example.test");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync("Browser billing password 42!");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(app.Address + "/");
        await page.GotoAsync(app.Address + "/settings");

        var panels = page.Locator("#panels");
        var diagram = panels.Locator(".roof-sun-diagram");
        await Assertions.Expect(diagram).ToBeVisibleAsync();
        await panels.GetByLabel("Installed capacity", new() { Exact = true }).First.FillAsync("4.5");
        var roof = diagram.Locator("[data-roof='1'] > g");
        await Assertions.Expect(roof).ToHaveCountAsync(1);
        await panels.GetByLabel("Azimuth", new() { Exact = true }).First.FillAsync("90");
        await Assertions.Expect(roof).ToHaveAttributeAsync("transform", "rotate(90)");
        await panels.GetByLabel("Tilt", new() { Exact = true }).First.FillAsync("45");
        await Assertions.Expect(diagram.Locator(".roof-sun-roofs svg g").First).ToHaveAttributeAsync("transform", "translate(20 60) rotate(-45)");

        var path = diagram.Locator(".roof-sun-path").First;
        await Assertions.Expect(path).ToHaveCountAsync(1);
        var originalPath = await path.GetAttributeAsync("d");
        Assert.False(string.IsNullOrWhiteSpace(originalPath));
        await page.GetByLabel("Latitude", new() { Exact = true }).FillAsync("-33.87");
        await Assertions.Expect(path).Not.ToHaveAttributeAsync("d", originalPath!);
        await page.GetByLabel("Longitude", new() { Exact = true }).FillAsync("0");

        var contract = page.Locator("#contract");
        await contract.GetByLabel("Price source", new() { Exact = true }).SelectOptionAsync("manual");
        await contract.GetByLabel("Sale price", new() { Exact = true }).FillAsync("0.42");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText("Installation settings saved.", new() { Exact = true })).ToBeVisibleAsync();
        await page.ReloadAsync();
        await Assertions.Expect(contract.GetByLabel("Price source", new() { Exact = true })).ToHaveValueAsync("manual");
        await Assertions.Expect(contract.GetByLabel("Sale price", new() { Exact = true })).ToHaveValueAsync("0.42");
        await Assertions.Expect(roof).ToHaveAttributeAsync("transform", "rotate(90)");
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
        await Assertions.Expect(page.Locator("#blazor-error-ui")).ToBeHiddenAsync();
        if (Environment.GetEnvironmentVariable("SOLAR_FEEDBACK_QA_DIRECTORY") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            await diagram.ScreenshotAsync(new() { Path = Path.Combine(folder, "roof-settings-mobile.png") });
            await contract.ScreenshotAsync(new() { Path = Path.Combine(folder, "fixed-price-settings-mobile.png") });
        }
    }
}
