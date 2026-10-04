using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using DeyeSolar.Web.Data;
namespace DeyeSolar.Web.Tests;
public class BrowserSecurityRevocationTests
{
    [SqlServerFact]
    public async Task ExistingBlazorCircuitClosesAfterSessionRevocationWithoutNavigation()
        => await VerifyRevocationAsync("session");
    [SqlServerFact]
    public async Task ExistingBlazorCircuitClosesAfterMembershipRemovalWithoutNavigation()
        => await VerifyRevocationAsync("membership");
    [SqlServerFact]
    public async Task ExistingBlazorCircuitClosesAfterSecurityStampChangeWithoutNavigation()
        => await VerifyRevocationAsync("stamp");
    [SqlServerFact]
    public async Task AccountAuthenticationSurvivesRemovalAndNewSignInWithNoTenantWhileEveryPrivateApiIsForbidden()
    {
        await using var app = await BrowserBillingTests.ProductionApp.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Address) };
        async Task<string> LoginAsync()
        {
            using var response = await client.PostAsJsonAsync("/api/auth/login", new { username = "billing-browser@example.test", password = "Browser billing password 42!" });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return payload.RootElement.GetProperty("token").GetString()!;
        }
        var originalToken = await LoginAsync();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", originalToken);
        await using (var db = new DeyeSolarDbContext(app.DatabaseOptions))
            await db.InstallationMemberships.Where(m => m.UserId == "browser-billing-owner").ExecuteDeleteAsync();
        foreach (var path in new[] { "/api/auth/session", "/api/billing/access", "/api/auth/security/permissions" })
        { using var allowed = await client.GetAsync(path); Assert.Equal(HttpStatusCode.OK, allowed.StatusCode); }
        foreach (var path in new[] { "/api/devices", "/api/rules", "/api/settings", "/api/readings", "/api/v2/integrations" })
        { using var denied = await client.GetAsync(path); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); }
        // An explicit new sign-in may authenticate the account with no tenant at all.
        client.DefaultRequestHeaders.Authorization = null;
        var accountToken = await LoginAsync();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accountToken);
        using var billing = await client.GetAsync("/api/billing/access"); Assert.Equal(HttpStatusCode.OK, billing.StatusCode);
        using var devices = await client.GetAsync("/api/devices"); Assert.Equal(HttpStatusCode.Forbidden, devices.StatusCode);
        await using var check = new DeyeSolarDbContext(app.DatabaseOptions);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(accountToken)));
        Assert.Null((await check.AccountSessions.SingleAsync(session => session.TokenHash == hash)).InstallationId);
    }
    private static async Task VerifyRevocationAsync(string kind)
    {
        await using var app = await BrowserBillingTests.ProductionApp.StartAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { Locale = "en-US" });
        await context.RouteAsync("**/*", r => r.Request.Url.StartsWith(app.Address, StringComparison.Ordinal) ? r.ContinueAsync() : r.AbortAsync());
        var page = await context.NewPageAsync();
        await page.GotoAsync(app.Address + "/login");
        await page.Locator("#username").FillAsync("billing-browser@example.test");
        await page.Locator("#password").FillAsync("Browser billing password 42!");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign In", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(app.Address + "/");
        await page.GotoAsync(app.Address + "/devices");
        var refresh = page.GetByRole(AriaRole.Button, new() { Name = "Refresh devices", Exact = true });
        await Assertions.Expect(refresh).ToBeVisibleAsync();
        await refresh.ClickAsync();
        await Assertions.Expect(page.GetByText("No smart sockets found. Add and enable a socket integration in Settings, then refresh devices.", new() { Exact = true })).ToBeVisibleAsync();
        var before = await app.ReadPrivateStateAsync();
        await using (var db = new DeyeSolarDbContext(app.DatabaseOptions))
        {
            const string user = "browser-billing-owner";
            if (kind == "session") await db.AccountSessions.Where(s => s.UserId == user).ExecuteDeleteAsync();
            else if (kind == "membership") await db.InstallationMemberships.Where(m => m.UserId == user).ExecuteDeleteAsync();
            else await db.Users.Where(u => u.Id == user).ExecuteUpdateAsync(s => s.SetProperty(u => u.SecurityStamp, "revoked-stamp"));
        }
        // No reload/navigation: the same open SignalR circuit must stop showing private content.
        await Assertions.Expect(refresh).ToHaveCountAsync(0, new() { Timeout = 20_000 });
        // The gate closes private content; a reconnect denied by the freshly validated
        // cookie may also send the existing browser page straight to sign-in.
        var closed = page.GetByText("Your installation could not be opened. Please sign in again or contact support.", new() { Exact = true });
        await Assertions.Expect(closed.Or(page.GetByRole(AriaRole.Button, new() { Name = "Sign In", Exact = true }))).ToBeVisibleAsync();
        var denied = await context.APIRequest.GetAsync(app.Address + "/api/devices?refresh=true");
        Assert.Contains(denied.Status, new[] { 401, 403 });
        if (kind == "membership")
        {
            var billing = await context.APIRequest.GetAsync(app.Address + "/api/billing/access"); Assert.Equal(200, billing.Status);
            var account = await context.APIRequest.GetAsync(app.Address + "/account"); Assert.Equal(200, account.Status);
        }
        Assert.Equal(before, await app.ReadPrivateStateAsync());
    }
}
