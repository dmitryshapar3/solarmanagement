using Microsoft.Playwright;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DeyeSolar.Web.Tests;

public sealed class SettingsFeedbackBrowserTests
{
    [SqlServerFact]
    public async Task RoofPreviewUsesExactPanelCountsAndRowsAndAllSettingsSurviveReload()
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
        await BrowserBillingDeadlineTests.AssertLiveDrawerEventAsync(page,
            page.GetByRole(AriaRole.Button, new() { Name = "Open navigation", Exact = true }));
        var closeNavigation = page.GetByRole(AriaRole.Button, new() { Name = "Close navigation", Exact = true });
        var backdropBounds = await closeNavigation.BoundingBoxAsync();
        Assert.NotNull(backdropBounds);
        // At 390px the drawer covers the backdrop centre; its right edge remains exposed.
        await closeNavigation.ClickAsync(new() { Position = new() { X = backdropBounds.Width - 12, Y = backdropBounds.Height / 2 } });
        await Assertions.Expect(closeNavigation).ToBeHiddenAsync();

        var panels = page.Locator("#panels");
        var diagram = panels.Locator(".roof-sun-diagram");
        await Assertions.Expect(diagram).ToBeVisibleAsync();
        var scene = diagram.Locator(".roof-scene-interactive");
        await Assertions.Expect(scene).ToBeVisibleAsync();
        await panels.GetByLabel("Installed capacity", new() { Exact = true }).First.FillAsync("4.5");
        await panels.GetByLabel("Installed capacity", new() { Exact = true }).Nth(1).FillAsync("3.5");
        var counts = panels.GetByLabel("Panel count", new() { Exact = true });
        var rows = panels.GetByLabel("Panels per row", new() { Exact = true });
        await counts.First.FillAsync("8");
        await counts.Nth(1).FillAsync("7");
        await rows.First.FillAsync("2");
        await rows.Nth(1).FillAsync("2");
        await Assertions.Expect(scene.Locator(".roof-scene-panel[data-roof='1']")).ToHaveCountAsync(8);
        await Assertions.Expect(scene.Locator(".roof-scene-panel[data-roof='2']")).ToHaveCountAsync(7);
        await counts.First.FillAsync("8.5");
        await Assertions.Expect(counts.First).ToHaveAttributeAsync("aria-invalid", "true");
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true })).ToBeDisabledAsync();
        await Assertions.Expect(scene.Locator(".roof-scene-panel[data-roof='1']")).ToHaveCountAsync(8);
        await counts.First.FillAsync("7");
        await Assertions.Expect(scene.Locator(".roof-scene-panel[data-roof='1']")).ToHaveCountAsync(7);
        var roof = scene.Locator(".roof-scene-roof[data-roof='1']");
        var secondRoof = scene.Locator(".roof-scene-roof[data-roof='2']");
        await Assertions.Expect(roof).ToHaveCountAsync(1);
        await Assertions.Expect(secondRoof).ToHaveCountAsync(1);
        var azimuths = panels.GetByLabel("Azimuth", new() { Exact = true });
        await azimuths.First.FillAsync("230");
        await azimuths.Nth(1).FillAsync("50");
        await Assertions.Expect(scene).ToHaveAttributeAsync("data-yaw", "-40");
        var originalRoof = await roof.GetAttributeAsync("points");
        var originalSecondRoof = await secondRoof.GetAttributeAsync("points");
        var path = scene.Locator(".roof-scene-sun-path").First;
        await Assertions.Expect(path).ToHaveCountAsync(1);
        var originalPath = await path.GetAttributeAsync("points");
        Assert.False(string.IsNullOrWhiteSpace(originalPath));
        // A common physical bearing change moves the sky, while the gable stays facing us.
        await azimuths.First.FillAsync("270");
        await Assertions.Expect(scene).ToHaveAttributeAsync("data-yaw", "0");
        await azimuths.Nth(1).FillAsync("90");
        await Assertions.Expect(path).Not.ToHaveAttributeAsync("points", originalPath!);
        await AssertRoofProjectionNearAsync(page, 1, originalRoof!);
        await AssertRoofProjectionNearAsync(page, 2, originalSecondRoof!);
        var bearingRoof = await roof.GetAttributeAsync("points");
        var tilts = panels.GetByLabel("Tilt", new() { Exact = true });
        await tilts.First.FillAsync("45");
        await Assertions.Expect(roof).Not.ToHaveAttributeAsync("points", bearingRoof!);

        await diagram.GetByRole(AriaRole.Button, new() { Name = "Zoom in", Exact = true }).ClickAsync();
        await Assertions.Expect(scene).Not.ToHaveAttributeAsync("data-zoom", "1");
        var zoom = await scene.GetAttributeAsync("data-zoom");
        var zoomedRoof = await roof.GetAttributeAsync("points");
        var zoomedSecondRoof = await secondRoof.GetAttributeAsync("points");
        await diagram.GetByRole(AriaRole.Button, new() { Name = "Set house direction", Exact = true }).ClickAsync();
        await Assertions.Expect(scene).ToHaveAttributeAsync("data-direction-editing", "true");
        var directionRight = diagram.GetByRole(AriaRole.Button, new() { Name = "Rotate sun path right", Exact = true });
        await directionRight.ClickAsync();
        await directionRight.ClickAsync();
        await Assertions.Expect(azimuths.First).ToHaveValueAsync("290");
        await Assertions.Expect(azimuths.Nth(1)).ToHaveValueAsync("110");
        await Assertions.Expect(scene).ToHaveAttributeAsync("data-yaw", "20");
        await diagram.GetByRole(AriaRole.Button, new() { Name = "Rotate sun path left", Exact = true }).ClickAsync();
        await Assertions.Expect(azimuths.First).ToHaveValueAsync("280");
        await Assertions.Expect(azimuths.Nth(1)).ToHaveValueAsync("100");
        await Assertions.Expect(scene).ToHaveAttributeAsync("data-yaw", "10");
        await AssertRoofProjectionNearAsync(page, 1, zoomedRoof!);
        await AssertRoofProjectionNearAsync(page, 2, zoomedSecondRoof!);
        await diagram.GetByRole(AriaRole.Button, new() { Name = "Done", Exact = true }).ClickAsync();
        await Assertions.Expect(scene).ToHaveAttributeAsync("data-direction-editing", "false");

        originalPath = await path.GetAttributeAsync("points");
        await diagram.GetByRole(AriaRole.Button, new() { Name = "Rotate sun path clockwise", Exact = true }).ClickAsync();
        await Assertions.Expect(scene).ToHaveAttributeAsync("data-sun-roll", "10");
        await Assertions.Expect(path).Not.ToHaveAttributeAsync("points", originalPath!);
        await AssertRoofProjectionNearAsync(page, 1, zoomedRoof!);
        await AssertRoofProjectionNearAsync(page, 2, zoomedSecondRoof!);
        await diagram.GetByRole(AriaRole.Button, new() { Name = "Rotate sun path counterclockwise", Exact = true }).ClickAsync();
        await Assertions.Expect(scene).ToHaveAttributeAsync("data-sun-roll", "0");
        await Assertions.Expect(path).ToHaveAttributeAsync("points", originalPath!);
        await diagram.GetByRole(AriaRole.Button, new() { Name = "Rotate sun path clockwise", Exact = true }).ClickAsync();
        await Assertions.Expect(scene).ToHaveAttributeAsync("data-sun-roll", "10");
        await Assertions.Expect(azimuths.First).ToHaveValueAsync("280");
        await Assertions.Expect(azimuths.Nth(1)).ToHaveValueAsync("100");
        await Assertions.Expect(tilts.First).ToHaveValueAsync("45");
        originalPath = await path.GetAttributeAsync("points");
        Assert.False(string.IsNullOrWhiteSpace(originalPath));
        await page.GetByLabel("Latitude", new() { Exact = true }).FillAsync("-33.87");
        await Assertions.Expect(path).Not.ToHaveAttributeAsync("points", originalPath!);
        await page.GetByLabel("Longitude", new() { Exact = true }).FillAsync("0");
        await Assertions.Expect(scene).ToHaveAttributeAsync("data-zoom", zoom!);
        await Assertions.Expect(scene).ToHaveAttributeAsync("data-sun-roll", "10");
        await diagram.GetByRole(AriaRole.Button, new() { Name = "Reset view", Exact = true }).ClickAsync();
        await Assertions.Expect(scene).ToHaveAttributeAsync("data-yaw", "10");
        await Assertions.Expect(scene).ToHaveAttributeAsync("data-zoom", "1");
        await Assertions.Expect(scene).ToHaveAttributeAsync("data-sun-roll", "0");
        await Assertions.Expect(azimuths.First).ToHaveValueAsync("280");
        await Assertions.Expect(azimuths.Nth(1)).ToHaveValueAsync("100");
        await Assertions.Expect(tilts.First).ToHaveValueAsync("45");
        var savedRoof = await roof.GetAttributeAsync("points");

        var contract = page.Locator("#contract");
        await contract.GetByLabel("Price source", new() { Exact = true }).SelectOptionAsync("manual");
        await contract.GetByLabel("Sale price", new() { Exact = true }).FillAsync("0.42");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText("Installation settings saved.", new() { Exact = true })).ToBeVisibleAsync();
        await page.ReloadAsync();
        await Assertions.Expect(contract.GetByLabel("Price source", new() { Exact = true })).ToHaveValueAsync("manual");
        await Assertions.Expect(contract.GetByLabel("Sale price", new() { Exact = true })).ToHaveValueAsync("0.42");
        await Assertions.Expect(counts.First).ToHaveValueAsync("7");
        await Assertions.Expect(counts.Nth(1)).ToHaveValueAsync("7");
        await Assertions.Expect(rows.First).ToHaveValueAsync("2");
        await Assertions.Expect(rows.Nth(1)).ToHaveValueAsync("2");
        await Assertions.Expect(azimuths.First).ToHaveValueAsync("280");
        await Assertions.Expect(azimuths.Nth(1)).ToHaveValueAsync("100");
        await Assertions.Expect(tilts.First).ToHaveValueAsync("45");
        await Assertions.Expect(page.GetByLabel("Latitude", new() { Exact = true })).ToHaveValueAsync("-33.87");
        await Assertions.Expect(page.GetByLabel("Longitude", new() { Exact = true })).ToHaveValueAsync("0");
        await Assertions.Expect(scene).ToHaveAttributeAsync("data-yaw", "10");
        await Assertions.Expect(scene).ToHaveAttributeAsync("data-sun-roll", "0");
        await Assertions.Expect(scene.Locator(".roof-scene-panel[data-roof='1']")).ToHaveCountAsync(7);
        await Assertions.Expect(scene.Locator(".roof-scene-panel[data-roof='2']")).ToHaveCountAsync(7);
        await Assertions.Expect(roof).ToHaveAttributeAsync("points", savedRoof!);
        // Authenticated server rendering must use the persisted counts even before the scene module mounts.
        var response = await context.APIRequest.GetAsync(app.Address + "/settings");
        Assert.Equal(200, response.Status);
        var serverHtml = await response.TextAsync();
        Assert.Equal(7, Regex.Matches(serverHtml, "class=\"roof-scene-face roof-scene-panel\" data-roof=\"1\"").Count);
        Assert.Equal(7, Regex.Matches(serverHtml, "class=\"roof-scene-face roof-scene-panel\" data-roof=\"2\"").Count);
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
        await Assertions.Expect(page.Locator("#blazor-error-ui")).ToBeHiddenAsync();
        if (Environment.GetEnvironmentVariable("SOLAR_FEEDBACK_QA_DIRECTORY") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            await diagram.ScreenshotAsync(new() { Path = Path.Combine(folder, "roof-settings-mobile.png") });
            await contract.ScreenshotAsync(new() { Path = Path.Combine(folder, "fixed-price-settings-mobile.png") });
        }
    }

    private static async Task AssertRoofProjectionNearAsync(IPage page, int roof, string expectedPoints)
    {
        var selector = $"#panels .roof-scene-interactive .roof-scene-roof[data-roof='{roof}']";
        var expected = expectedPoints.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        Assert.NotEmpty(expected);
        // Renderer coordinates have three decimals; tolerate only rounding at that precision.
        await page.WaitForFunctionAsync("""
            ({selector, expected}) => {
                const element = document.querySelector(selector);
                if (!element) return false;
                const actual = (element.getAttribute('points') || '').trim().split(/[ ,]+/).map(Number);
                return actual.length === expected.length && actual.every((value, i) =>
                    Number.isFinite(value) && Math.abs(value - expected[i]) <= 0.002);
            }
            """, new { selector, expected }, new() { Timeout = 5_000 });
        var actual = (await page.Locator(selector).GetAttributeAsync("points"))!
            .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++) Assert.InRange(Math.Abs(actual[i] - expected[i]), 0, 0.002);
    }
}
