using System.Text;
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
        var renders = new BlazorRenderObserver(page);
        await renders.RunAsync(() => page.GotoAsync(app.Address + "/energy"), requireNewCircuit: true);
        var filters = page.GetByTestId("energy-period-filters");
        foreach (var label in new[] { "Day", "7 days", "30 days", "Month", "Custom" })
            await Assertions.Expect(filters.GetByRole(AriaRole.Button, new() { Name = label, Exact = true })).ToBeVisibleAsync();
        await renders.RunAsync(() => filters.GetByRole(AriaRole.Button, new() { Name = "Month", Exact = true }).ClickAsync());
        await Assertions.Expect(filters.GetByLabel("Date", new() { Exact = true })).ToHaveAttributeAsync("type", "month");
        await renders.RunAsync(() => filters.GetByLabel("Date", new() { Exact = true }).FillAsync("2026-09"));
        await Assertions.Expect(page).ToHaveURLAsync(new Regex("period=month&date=2026-09-01"), new() { Timeout = 20_000 });
        await renders.RunAsync(() => page.Locator("nav.segments").GetByRole(AriaRole.Link, new() { Name = "Export", Exact = true }).ClickAsync());
        await Assertions.Expect(page).ToHaveURLAsync(new Regex("/energy/export\\?period=month&date=2026-09-01"), new() { Timeout = 20_000 });
        await Assertions.Expect(filters.GetByLabel("Date", new() { Exact = true })).ToHaveValueAsync("2026-09");
        await renders.RunAsync(() => filters.GetByRole(AriaRole.Button, new() { Name = "Custom", Exact = true }).ClickAsync());
        await filters.GetByLabel("From", new() { Exact = true }).FillAsync("2026-09-04");
        await filters.GetByLabel("To", new() { Exact = true }).FillAsync("2026-09-12");
        await renders.RunAsync(() => filters.GetByRole(AriaRole.Button, new() { Name = "Apply", Exact = true }).ClickAsync());
        await Assertions.Expect(page).ToHaveURLAsync(new Regex("from=2026-09-04&through=2026-09-12"), new() { Timeout = 20_000 });
        await renders.RunAsync(() => page.Locator("nav.segments").GetByRole(AriaRole.Link, new() { Name = "Production", Exact = true }).ClickAsync());
        await Assertions.Expect(filters.GetByLabel("From", new() { Exact = true })).ToHaveValueAsync("2026-09-04");
        await Assertions.Expect(filters.GetByLabel("To", new() { Exact = true })).ToHaveValueAsync("2026-09-12");
        await renders.RunAsync(() => filters.GetByRole(AriaRole.Button, new() { Name = "Next 7 days", Exact = true }).ClickAsync());
        await Assertions.Expect(page).ToHaveURLAsync(new Regex("period=7d"), new() { Timeout = 20_000 });
        await renders.RunAsync(() => filters.GetByRole(AriaRole.Button, new() { Name = "Next period", Exact = true }).ClickAsync());
        await Assertions.Expect(page.Locator("#blazor-error-ui")).ToBeHiddenAsync();
        await page.SetViewportSizeAsync(390, 844);
        await renders.RunAsync(() => filters.GetByRole(AriaRole.Button, new() { Name = "Next 30 days", Exact = true }).ClickAsync());
        await Assertions.Expect(page).ToHaveURLAsync(new Regex("period=30d"), new() { Timeout = 20_000 });
        await Assertions.Expect(filters.GetByLabel("Date", new() { Exact = true })).ToBeVisibleAsync();
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"));
        await renders.RunAsync(() => page.Locator("nav.segments").GetByRole(AriaRole.Link, new() { Name = "Export", Exact = true }).ClickAsync());
        await Assertions.Expect(page).ToHaveURLAsync(new Regex("/energy/export\\?period=30d"), new() { Timeout = 20_000 });
        await Assertions.Expect(page.GetByText("Future export and value are unavailable until measured. View the generation forecast on the Production tab.", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("#blazor-error-ui")).ToBeHiddenAsync();
        var screenshotDirectory = Environment.GetEnvironmentVariable("SOLAR_ENERGY_QA_DIRECTORY");
        if (screenshotDirectory is not null)
        {
            Directory.CreateDirectory(screenshotDirectory);
            await page.ScreenshotAsync(new() { Path = Path.Combine(screenshotDirectory, "energy-export-mobile.png"), FullPage = true });
            await page.SetViewportSizeAsync(1440, 1000);
            await renders.RunAsync(() => page.Locator("nav.segments").GetByRole(AriaRole.Link, new() { Name = "Production", Exact = true }).ClickAsync());
            await page.ScreenshotAsync(new() { Path = Path.Combine(screenshotDirectory, "energy-production-desktop.png"), FullPage = true });
        }

        // Use the real saved account preference so the long Russian labels are rendered
        // by the application, including after changing tabs and reconnecting the circuit.
        await page.GotoAsync(app.Address + "/settings/account");
        await page.GetByLabel("Language", new() { Exact = true }).SelectOptionAsync("ru");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save preferences", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(new Regex("/settings/account\\?preferences=saved"));
        await page.SetViewportSizeAsync(390, 844);
        await renders.RunAsync(() => page.GotoAsync(app.Address + "/energy"), requireNewCircuit: true);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Энергия", Exact = true })).ToBeVisibleAsync();
        foreach (var tab in new[] { "Выработка", "Отдача в сеть" })
        {
            await renders.RunAsync(() => page.Locator("nav.segments").GetByRole(AriaRole.Link, new() { Name = tab, Exact = true }).ClickAsync());
            foreach (var label in new[] { "День", "7 дней", "30 дней", "Месяц", "Пользовательский" })
            {
                var button = filters.GetByRole(AriaRole.Button, new() { Name = label, Exact = true });
                await renders.RunAsync(() => button.ClickAsync());
                await Assertions.Expect(button).ToHaveAttributeAsync("aria-pressed", "true");
                await Assertions.Expect(page.Locator("#blazor-error-ui")).ToBeHiddenAsync();
                Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"),
                    "Russian mobile Energy content overflows after selecting " + label + " on " + tab);
                // Navigation recreates the filter row, so scroll the selected control
                // into view again before checking that every button is fully reachable.
                await button.EvaluateAsync("element => element.scrollIntoView({ block: 'nearest', inline: 'nearest' })");
                var bounds = await button.BoundingBoxAsync();
                Assert.NotNull(bounds);
                Assert.InRange(bounds.X, 0, 390 - bounds.Width);
            }
            var segments = filters.Locator(".energy-period-segments");
            Assert.True(await segments.EvaluateAsync<bool>("element => element.scrollWidth > element.clientWidth"),
                "The long Russian filter labels should scroll within their own row.");
            await Assertions.Expect(filters.GetByLabel("От", new() { Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(filters.GetByLabel("По", new() { Exact = true })).ToBeVisibleAsync();
            if (screenshotDirectory is not null)
                await page.ScreenshotAsync(new() { Path = Path.Combine(screenshotDirectory,
                    tab == "Выработка" ? "energy-production-ru-mobile.png" : "energy-export-ru-mobile.png"), FullPage = true });
        }
    }

    private sealed class BlazorRenderObserver
    {
        private IWebSocket? _currentSocket;
        private event Action<IWebSocket>? RenderCompleted;

        public BlazorRenderObserver(IPage page)
        {
            page.WebSocket += (_, socket) =>
            {
                if (!socket.Url.Contains("_blazor", StringComparison.Ordinal)) return;
                _currentSocket = socket;
                socket.FrameSent += (_, frame) =>
                {
                    var payload = frame.Text ?? Encoding.UTF8.GetString(frame.Binary ?? []);
                    if (payload.Contains("OnRenderCompleted", StringComparison.Ordinal))
                        RenderCompleted?.Invoke(socket);
                };
            };
        }

        public async Task RunAsync(Func<Task> action, bool requireNewCircuit = false)
        {
            var previousSocket = _currentSocket;
            var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnRenderCompleted(IWebSocket socket)
            {
                if (!requireNewCircuit || socket != previousSocket) rendered.TrySetResult();
            }
            RenderCompleted += OnRenderCompleted;
            try
            {
                await action();
                // A handshake or prerendered DOM does not mean event handlers are ready.
                // This is the client's acknowledgment of an applied interactive render.
                await rendered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            }
            finally
            {
                RenderCompleted -= OnRenderCompleted;
            }
        }
    }
}
