using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace DeyeSolar.Web.Tests;

public sealed class EnergyFiltersBrowserTests
{
    [SqlServerFact]
    public async Task BothEnergyTabsKeepMonthCustomAndFutureWindowsOnDesktopAndMobile()
    {
        await using var app = await BrowserBillingTests.ProductionApp.StartAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { Locale = "en-US", ViewportSize = new() { Width = 1440, Height = 1000 } });
        await context.RouteAsync("**/*", r => r.Request.Url.StartsWith(app.Address, StringComparison.Ordinal) ? r.ContinueAsync() : r.AbortAsync());
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        await page.GotoAsync(app.Address + "/signin?mode=password");
        await page.GetByLabel("Username, email or phone", new() { Exact = true }).FillAsync("billing-browser@example.test");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync("Browser billing password 42!");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(app.Address + "/");
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        page.WebSocket += (_, socket) => { if (socket.Url.Contains("_blazor", StringComparison.Ordinal)) socket.FrameReceived += (_, _) => connected.TrySetResult(); };
        await page.GotoAsync(app.Address + "/energy");
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var filters = page.GetByTestId("energy-period-filters");
        foreach (var label in new[] { "Day", "7 days", "30 days", "Month", "Custom" })
            await Assertions.Expect(filters.GetByRole(AriaRole.Button, new() { Name = label, Exact = true })).ToBeVisibleAsync();
        await filters.GetByRole(AriaRole.Button, new() { Name = "Month", Exact = true }).ClickAsync();
        await Assertions.Expect(filters.GetByLabel("Date", new() { Exact = true })).ToHaveAttributeAsync("type", "month");
        await filters.GetByLabel("Date", new() { Exact = true }).FillAsync("2026-09");
        await Assertions.Expect(page).ToHaveURLAsync(new Regex("period=month&date=2026-09-01"));
        await page.Locator("nav.segments").GetByRole(AriaRole.Link, new() { Name = "Export", Exact = true }).ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(new Regex("/energy/export\\?period=month&date=2026-09-01"));
        await Assertions.Expect(filters.GetByLabel("Date", new() { Exact = true })).ToHaveValueAsync("2026-09");
        await filters.GetByRole(AriaRole.Button, new() { Name = "Custom", Exact = true }).ClickAsync();
        await filters.GetByLabel("From", new() { Exact = true }).FillAsync("2026-09-04");
        await filters.GetByLabel("To", new() { Exact = true }).FillAsync("2026-09-12");
        await filters.GetByRole(AriaRole.Button, new() { Name = "Apply", Exact = true }).ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(new Regex("from=2026-09-04&through=2026-09-12"));
        await page.Locator("nav.segments").GetByRole(AriaRole.Link, new() { Name = "Production", Exact = true }).ClickAsync();
        await Assertions.Expect(filters.GetByLabel("From", new() { Exact = true })).ToHaveValueAsync("2026-09-04");
        await Assertions.Expect(filters.GetByLabel("To", new() { Exact = true })).ToHaveValueAsync("2026-09-12");
        await filters.GetByRole(AriaRole.Button, new() { Name = "Next 7 days", Exact = true }).ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(new Regex("period=7d"));
        await filters.GetByRole(AriaRole.Button, new() { Name = "Next period", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator("#blazor-error-ui")).ToBeHiddenAsync();
        await page.SetViewportSizeAsync(390, 844);
        await filters.GetByRole(AriaRole.Button, new() { Name = "Next 30 days", Exact = true }).ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(new Regex("period=30d"));
        await Assertions.Expect(filters.GetByLabel("Date", new() { Exact = true })).ToBeVisibleAsync();
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"));
        await page.Locator("nav.segments").GetByRole(AriaRole.Link, new() { Name = "Export", Exact = true }).ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(new Regex("/energy/export\\?period=30d"));
        await Assertions.Expect(page.GetByText("Future export and value are unavailable until measured. View the generation forecast on the Production tab.", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("#blazor-error-ui")).ToBeHiddenAsync();
        var screenshotDirectory = Environment.GetEnvironmentVariable("SOLAR_ENERGY_QA_DIRECTORY");
        if (screenshotDirectory is not null)
        {
            Directory.CreateDirectory(screenshotDirectory);
            await page.ScreenshotAsync(new() { Path = Path.Combine(screenshotDirectory, "energy-export-mobile.png"), FullPage = true });
            await page.SetViewportSizeAsync(1440, 1000);
            await page.Locator("nav.segments").GetByRole(AriaRole.Link, new() { Name = "Production", Exact = true }).ClickAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(screenshotDirectory, "energy-production-desktop.png"), FullPage = true });
        }
    }
}
