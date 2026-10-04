using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeyeSolar.Web.Api;
using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DeyeSolar.Web.Tests;

public class GoogleIdentitySqlServerTests
{
    private const string Password = "Local Google password 42!";
    private const string Verifier = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string State = "local_google_state_1234567890";
    private const string Neighbor = "google-neighbor";
    private static string Challenge => WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier)));

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("true", null, true)]
    [InlineData("false", null, false)]
    [InlineData("false", "true", true)]
    [InlineData("true", "false", false)]
    [InlineData("false", "false", false)]
    public void GoogleSignupOverrideInheritsOnlyWhenNotConfigured(string? registration, string? google, bool expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:RegistrationEnabled"] = registration,
            ["Auth:Google:RegistrationEnabled"] = google
        }).Build();
        var options = AuthProviderOptions.Capture(config);
        Assert.Equal(expected, options.AllowGoogleRegistration);
        Assert.Equal(registration != "false", options.RegistrationEnabled);
    }

    [SqlServerFact]
    public async Task GoogleSignupOverrideCreatesIsolatedAccountsWhilePasswordRegistrationRemainsClosed()
    {
        await using var host = await Host.StartAsync(true);
        var before = await host.PrivateDataAsync();
        var options = await host.Client.GetFromJsonAsync<JsonElement>("/api/auth/options");
        Assert.False(options.GetProperty("registrationEnabled").GetBoolean());
        Assert.True(options.GetProperty("googleEnabled").GetBoolean());
        Assert.True(options.GetProperty("googleRegistrationEnabled").GetBoolean());
        using (var denied = await host.Client.PostAsJsonAsync("/api/auth/register", new AccountRegisterRequest("unused", "000000", Password)))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
        var sessions = new List<MobileAuthResponse>();
        foreach (var suffix in new[] { "first", "second" })
        {
            var callback = await host.OAuthAsync(suffix, suffix + "@example.test");
            using var response = await host.ExchangeAsync(Code(callback), null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var session = (await response.Content.ReadFromJsonAsync<MobileAuthResponse>())!;
            sessions.Add(session);
            Assert.NotEqual(InstallationIds.Legacy, session.InstallationId);
            Assert.NotEqual(Neighbor, session.InstallationId);
            await using var own = host.Factory.ForInstallation(session.InstallationId!);
            Assert.Empty(await own.AppSettings.ToListAsync());
            Assert.Empty(await own.Readings.ToListAsync());
            Assert.Empty(await own.ExportReadings.ToListAsync());
            var user = await own.Users.SingleAsync(u => u.UserName == session.Username);
            Assert.True(user.EmailConfirmed);
            Assert.Null(user.PasswordHash);
            Assert.Equal(user.Id, (await own.UserLogins.SingleAsync(login => login.ProviderKey == suffix)).UserId);
            Assert.Equal(session.InstallationId, (await own.InstallationMemberships.SingleAsync(m => m.UserId == user.Id)).InstallationId);
        }
        Assert.NotEqual(sessions[0].InstallationId, sessions[1].InstallationId);
        Assert.Equal(before, await host.PrivateDataAsync());
        await using var db = host.Factory.CreateDbContext();
        Assert.Equal(4, await db.Users.CountAsync());
        Assert.Equal(4, await db.Installations.CountAsync());
        Assert.Equal(4, await db.InstallationMemberships.CountAsync());
    }

    [SqlServerFact]
    public async Task ClosedGoogleSignupStillRequiresExplicitLinkForExistingEmailAndAllowsKnownSubject()
    {
        await using var host = await Host.StartAsync(false);
        var before = await host.StateAsync();
        Assert.Equal("link_required", Error(await host.OAuthAsync("unbound", "NEIGHBOR@example.test")));
        Assert.Equal("registration_disabled", Error(await host.OAuthAsync("new", "new@example.test")));
        Assert.Equal("google_failed", Error(await host.OAuthAsync("unverified", "neighbor@example.test", verified: false)));
        Assert.Equal(before, await host.StateAsync());
        await host.AddLoginAsync(host.OwnerId, "known");
        before = await host.StateAsync();
        using var response = await host.ExchangeAsync(Code(await host.OAuthAsync("known", "neighbor@example.test")), null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(InstallationIds.Legacy, (await response.Content.ReadFromJsonAsync<MobileAuthResponse>())!.InstallationId);
        Assert.Equal(before, await host.StateAsync());
    }

    [SqlServerFact]
    public async Task UsernameOnlyAccountLinksVerifiedGoogleEmailOnlyAfterSameBearerAndPkceProof()
    {
        await using var host = await Host.StartAsync(false);
        var before = await host.StateAsync();
        var privateBefore = await host.PrivateDataAsync();
        var ownerBefore = await host.OwnerInvariantAsync();
        var callback = await host.OAuthAsync("owner-google", "OWNER@example.test", host.OwnerToken);
        var code = Code(callback);
        Assert.Equal(before, await host.StateAsync());
        foreach (var token in new string?[] { null, "forged", host.NeighborToken })
        {
            using var denied = await host.ExchangeAsync(code, token);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            Assert.Equal(before, await host.StateAsync());
        }
        using (var wrong = await host.ExchangeAsync(code, host.OwnerToken, new string('b', 64)))
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        using (var linked = await host.ExchangeAsync(code, host.OwnerToken))
        {
            Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
            Assert.Equal("username-only", (await linked.Content.ReadFromJsonAsync<MobileAuthResponse>())!.Username);
        }
        await using (var db = host.Factory.CreateDbContext())
        {
            var owner = await db.Users.SingleAsync(u => u.Id == host.OwnerId);
            Assert.Equal("owner@example.test", owner.Email);
            Assert.True(owner.EmailConfirmed);
            Assert.Equal(host.OwnerId, (await db.UserLogins.SingleAsync()).UserId);
            Assert.Equal(2, await db.Users.CountAsync());
            Assert.Equal(2, await db.Installations.CountAsync());
        }
        Assert.Equal(ownerBefore, await host.OwnerInvariantAsync());
        Assert.Equal(privateBefore, await host.PrivateDataAsync());
        var linkedState = await host.StateAsync();
        using (var replay = await host.ExchangeAsync(code, host.OwnerToken))
            Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        using (var known = await host.ExchangeAsync(Code(await host.OAuthAsync("owner-google", "owner@example.test")), null))
        {
            Assert.Equal(HttpStatusCode.OK, known.StatusCode);
            Assert.Equal(InstallationIds.Legacy, (await known.Content.ReadFromJsonAsync<MobileAuthResponse>())!.InstallationId);
        }
        Assert.Equal(linkedState, await host.StateAsync());
        using (var repeatedLink = await host.ExchangeAsync(Code(await host.OAuthAsync("owner-google", "owner@example.test", host.OwnerToken)), host.OwnerToken))
            Assert.Equal(HttpStatusCode.OK, repeatedLink.StatusCode);
        Assert.Equal(linkedState, await host.StateAsync());
    }

    [SqlServerFact]
    public async Task LinkingCannotClaimAnotherAccountsEmailOrGoogleSubject()
    {
        await using var host = await Host.StartAsync(true);
        await host.AddLoginAsync(host.NeighborId, "neighbor-subject");
        await host.AddLoginAsync(host.OwnerId, "owner-subject");
        var before = await host.StateAsync();
        foreach (var (subject, email) in new[] { ("unbound", "NEIGHBOR@example.test"), ("neighbor-subject", "free@example.test"), ("owner-subject", "neighbor@example.test") })
        {
            using var denied = await host.ExchangeAsync(Code(await host.OAuthAsync(subject, email, host.OwnerToken)), host.OwnerToken);
            Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
            Assert.Equal("link_conflict", (await denied.Content.ReadFromJsonAsync<IdentityApiError>())!.Code);
            Assert.Equal(before, await host.StateAsync());
        }
    }

    [SqlServerFact]
    public async Task LinkingPreservesDifferentEmailAndConfirmsMatchingUnverifiedEmail()
    {
        await using var host = await Host.StartAsync(true);
        await host.SetEmailAsync(host.OwnerId, "original@example.test", true);
        await host.SetEmailAsync(host.NeighborId, "neighbor@example.test", false);
        var before = await host.PrivateDataAsync(includeNeighbor: false);
        using (var linked = await host.ExchangeAsync(Code(await host.OAuthAsync("different", "new-google@example.test", host.OwnerToken)), host.OwnerToken))
            Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        using (var linked = await host.ExchangeAsync(Code(await host.OAuthAsync("matching", "NEIGHBOR@example.test", host.NeighborToken)), host.NeighborToken))
            Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        await using var db = host.Factory.CreateDbContext();
        Assert.Equal("original@example.test", (await db.Users.SingleAsync(u => u.Id == host.OwnerId)).Email);
        Assert.True((await db.Users.SingleAsync(u => u.Id == host.NeighborId)).EmailConfirmed);
        Assert.Equal(2, await db.UserLogins.CountAsync());
        Assert.Equal(before, await host.PrivateDataAsync(includeNeighbor: false));
    }

    [SqlServerFact]
    public async Task ExpiredAndConcurrentExchangePreserveOwnershipAndApplyOneLink()
    {
        await using var host = await Host.StartAsync(true);
        var before = await host.StateAsync();
        var expired = Code(await host.OAuthAsync("expired", "expired@example.test", host.OwnerToken));
        host.Clock.Advance(TimeSpan.FromMinutes(2));
        using (var denied = await host.ExchangeAsync(expired, host.OwnerToken)) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal(before, await host.StateAsync());
        var code = Code(await host.OAuthAsync("concurrent", "concurrent@example.test", host.OwnerToken));
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => host.ExchangeAsync(code, host.OwnerToken)));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Equal(3, responses.Count(response => response.StatusCode == HttpStatusCode.Unauthorized));
        }
        finally { foreach (var response in responses) response.Dispose(); }
        await using var db = host.Factory.CreateDbContext();
        Assert.Equal(host.OwnerId, (await db.UserLogins.SingleAsync()).UserId);
        Assert.Equal("concurrent@example.test", (await db.Users.SingleAsync(u => u.Id == host.OwnerId)).Email);
        Assert.Equal(2, await db.Installations.CountAsync());
    }

    [SqlServerFact]
    public async Task FailedGoogleLoginInsertRollsBackEmailAndNewAccountTogether()
    {
        await using var host = await Host.StartAsync(true, registration: true);
        await using (var db = host.Factory.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE AspNetUserLogins ADD CONSTRAINT CK_Test_RejectGoogle CHECK (ProviderKey <> 'rejected');");
        var before = await host.StateAsync();
        var callback = await host.OAuthAsync("rejected", "rollback@example.test", host.OwnerToken);
        using (var denied = await host.ExchangeAsync(Code(callback), host.OwnerToken)) Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        Assert.Equal(before, await host.StateAsync());
        Assert.Equal("link_required", Error(await host.OAuthAsync("rejected", "new-rollback@example.test")));
        Assert.Equal(before, await host.StateAsync());
    }

    [SqlServerFact]
    public async Task ConcurrentGoogleSignInsCreateOnlyOneAccountAndInstallation()
    {
        await using var host = await Host.StartAsync(true, registration: true);
        var before = await host.PrivateDataAsync();
        var callbacks = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => host.OAuthAsync("same-subject", "same@example.test")));
        Assert.Contains(callbacks, callback => QueryHelpers.ParseQuery(callback.Query).ContainsKey("code"));
        foreach (var callback in callbacks)
        {
            if (QueryHelpers.ParseQuery(callback.Query).ContainsKey("code"))
            {
                using var response = await host.ExchangeAsync(Code(callback), null);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            else Assert.Contains(Error(callback), new[] { "link_required", "registration_failed", "link_failed" });
        }
        await using var db = host.Factory.CreateDbContext();
        Assert.Equal(3, await db.Users.CountAsync());
        Assert.Equal(3, await db.Installations.CountAsync());
        Assert.Equal(3, await db.InstallationMemberships.CountAsync());
        Assert.Single(await db.UserLogins.ToListAsync());
        Assert.Equal(before, await host.PrivateDataAsync());
        var state = await host.StateAsync();
        using var retry = await host.ExchangeAsync(Code(await host.OAuthAsync("same-subject", "same@example.test")), null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(state, await host.StateAsync());
    }

    [SqlServerFact]
    public async Task WebCallbackCannotLinkAfterCookieOwnerIsLockedOut()
    {
        await using var host = await Host.StartAsync(true);
        string? lockedState = null;
        var callback = await host.WebOAuthAsync(async () =>
        {
            await using var db = host.Factory.CreateDbContext();
            var user = await db.Users.SingleAsync(u => u.Id == host.OwnerId);
            user.LockoutEnabled = true; user.LockoutEnd = DateTimeOffset.UtcNow.AddHours(1);
            await db.SaveChangesAsync();
            lockedState = await host.StateAsync();
        });
        Assert.Contains("error=account_unavailable", callback.ToString());
        await using var check = host.Factory.CreateDbContext();
        Assert.Empty(await check.UserLogins.ToListAsync());
        Assert.Null((await check.Users.SingleAsync(u => u.Id == host.OwnerId)).Email);
        Assert.Equal(lockedState, await host.StateAsync());
    }

    private static string Code(Uri callback)
    {
        Assert.Equal("deyesolar", callback.Scheme); Assert.Equal("auth", callback.Host); Assert.Equal("/callback", callback.AbsolutePath);
        var query = QueryHelpers.ParseQuery(callback.Query);
        Assert.Equal(State, query["state"].ToString()); Assert.False(query.ContainsKey("error"), callback.Query);
        Assert.False(string.IsNullOrEmpty(query["code"])); return query["code"].ToString();
    }
    private static string Error(Uri callback) => QueryHelpers.ParseQuery(callback.Query)["error"].ToString();

    private sealed class Host(WebApplication app, HttpClient client, Factory factory, Clock clock, GoogleBackchannel google) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public Factory Factory { get; } = factory;
        public Clock Clock { get; } = clock;
        public string OwnerId = "", NeighborId = "", OwnerToken = "", NeighborToken = "";

        public async Task<Uri> OAuthAsync(string subject, string email, string? token = null, bool verified = true)
        {
            var path = $"/auth/google?mobile=true&codeChallenge={Challenge}&state={State}";
            if (token is not null)
            {
                using var start = await SendAsync("/api/auth/google/link/start", new GoogleMobileLinkStartRequest(Challenge, State), token);
                Assert.Equal(HttpStatusCode.OK, start.StatusCode);
                path = new Uri((await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("authorizationUrl").GetString()!).PathAndQuery;
            }
            using var challenge = await Client.GetAsync(path);
            return await FinishOAuthAsync(challenge, subject, email, verified);
        }
        public async Task<Uri> WebOAuthAsync(Func<Task> beforeComplete)
        {
            using var challenge = await Client.GetAsync("/test/web-google");
            return await FinishOAuthAsync(challenge, "web-subject", "web@example.test", true, beforeComplete);
        }
        private async Task<Uri> FinishOAuthAsync(HttpResponseMessage challenge, string subject, string email, bool verified, Func<Task>? beforeComplete = null)
        {
            Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
            var protectedState = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["state"].ToString();
            var externalCode = google.Add(subject, email, verified);
            using var provider = await GetWithCookiesAsync("/signin-google?code=" + externalCode + "&state=" + Uri.EscapeDataString(protectedState), Cookies(challenge));
            Assert.Equal(HttpStatusCode.Redirect, provider.StatusCode);
            Assert.Equal("/auth/google/complete", provider.Headers.Location!.ToString());
            if (beforeComplete is not null) await beforeComplete();
            using var complete = await GetWithCookiesAsync("/auth/google/complete", Cookies(challenge) + "; " + Cookies(provider));
            Assert.Equal(HttpStatusCode.Redirect, complete.StatusCode);
            return complete.Headers.Location!;
        }
        private async Task<HttpResponseMessage> GetWithCookiesAsync(string path, string cookies)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path); request.Headers.Add("Cookie", cookies);
            return await Client.SendAsync(request);
        }
        private static string Cookies(HttpResponseMessage response) => string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0]));
        public Task<HttpResponseMessage> ExchangeAsync(string code, string? token, string verifier = Verifier)
            => SendAsync("/api/auth/google/exchange", new GoogleMobileExchangeRequest(code, verifier), token);
        private async Task<HttpResponseMessage> SendAsync<T>(string path, T body, string? token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            if (token is not null) request.Headers.Authorization = new("Bearer", token);
            return await Client.SendAsync(request);
        }
        public async Task AddLoginAsync(string userId, string subject)
        {
            using var scope = app.Services.CreateScope(); var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            Assert.True((await users.AddLoginAsync((await users.FindByIdAsync(userId))!, new("Google", subject, "Google"))).Succeeded);
        }
        public async Task SetEmailAsync(string userId, string email, bool confirmed)
        {
            using var scope = app.Services.CreateScope(); var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = (await users.FindByIdAsync(userId))!; user.Email = email; user.EmailConfirmed = confirmed;
            Assert.True((await users.UpdateAsync(user)).Succeeded);
        }
        public async Task<string> PrivateDataAsync(bool includeNeighbor = true)
        {
            await using var db = Factory.CreateDbContext();
            return JsonSerializer.Serialize(new
            {
                Settings = await db.AppSettings.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
                Readings = await db.Readings.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
                Exports = await db.ExportReadings.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.InstallationId).ThenBy(x => x.ObservedAt).ToListAsync(),
                Claims = await db.UserClaims.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
                Neighbor = includeNeighbor ? await db.Users.AsNoTracking().SingleAsync(x => x.Id == NeighborId) : null
            });
        }
        public async Task<string> OwnerInvariantAsync()
        {
            await using var db = Factory.CreateDbContext();
            return JsonSerializer.Serialize(await db.Users.Where(x => x.Id == OwnerId).Select(x => new { x.Id, x.UserName, x.PasswordHash, x.SecurityStamp }).SingleAsync());
        }
        public async Task<string> StateAsync()
        {
            await using var db = Factory.CreateDbContext();
            return JsonSerializer.Serialize(new
            {
                Private = await PrivateDataAsync(),
                Users = await db.Users.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
                Installations = await db.Installations.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
                Memberships = await db.InstallationMemberships.AsNoTracking().OrderBy(x => x.UserId).ThenBy(x => x.InstallationId).Select(x => new { x.UserId, x.InstallationId, x.Role }).ToListAsync(),
                Logins = await db.UserLogins.AsNoTracking().OrderBy(x => x.LoginProvider).ThenBy(x => x.ProviderKey).ToListAsync()
            });
        }
        public static async Task<Host> StartAsync(bool? googleRegistration, bool registration = false)
        {
            var connection = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SOLAR_TEST_SQL_CONNECTION"))
            { InitialCatalog = "SolarGoogleIdentityTests_" + Guid.NewGuid().ToString("N") };
            var factory = new Factory(new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlServer(connection.ConnectionString).Options);
            WebApplication? app = null;
            try
            {
                await using (var db = factory.CreateDbContext())
                {
                    await db.Database.MigrateAsync(); db.Installations.Add(new() { Id = Neighbor }); await db.SaveChangesAsync();
                }
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = AppContext.BaseDirectory });
                builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
                builder.Services.AddSingleton<IDbContextFactory<DeyeSolarDbContext>>(factory); builder.Services.AddScoped(_ => factory.CreateDbContext());
                builder.Services.AddIdentity<IdentityUser, IdentityRole>().AddEntityFrameworkStores<DeyeSolarDbContext>();
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Auth:RegistrationEnabled"] = registration.ToString(),
                    ["Auth:Google:RegistrationEnabled"] = googleRegistration?.ToString(),
                    ["Auth:Google:ClientId"] = "local-google",
                    ["Auth:Google:ClientSecret"] = "local-only-secret"
                }).Build();
                builder.Services.AddAccountIdentities(AuthProviderOptions.Capture(config));
                var google = new GoogleBackchannel();
                builder.Services.PostConfigure<GoogleOptions>(GoogleIdentityEndpoints.Scheme, options => options.Backchannel = new HttpClient(google));
                builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, MobileBearerAuthenticationHandler>(MobileBearerAuthenticationHandler.SchemeName, _ => { });
                builder.Services.AddAuthorization(); builder.Services.AddSingleton<MobileSessionStore>(); builder.Services.AddScoped<MobileAuthService>();
                var clock = new Clock(); builder.Services.AddSingleton<TimeProvider>(clock);
                app = builder.Build(); app.UseRouting(); app.UseRateLimiter(); app.UseAccountIdentityOrigin(); app.UseAuthentication(); app.UseAuthorization();
                app.MapAccountIdentityApi(); app.MapGoogleIdentity();
                // Test-only web setup creates a real Identity cookie; the real OAuth middleware and callback enforce it after lockout changes.
                app.MapGet("/test/web-google", async (SignInManager<IdentityUser> signIn, GoogleMobileTicketStore tickets) =>
                {
                    var user = (await signIn.UserManager.FindByNameAsync("username-only"))!;
                    await signIn.SignInAsync(user, true);
                    var properties = new AuthenticationProperties { RedirectUri = "/auth/google/complete" };
                    properties.Items["solar.link.user"] = user.Id;
                    properties.Items["solar.oauth.once"] = tickets.StartCallback();
                    return Results.Challenge(properties, [GoogleIdentityEndpoints.Scheme]);
                });
                await app.StartAsync();
                var address = Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
                var host = new Host(app, new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new(address) }, factory, clock, google);
                using (var scope = app.Services.CreateScope())
                {
                    var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
                    foreach (var (name, email, installation) in new[] { ("username-only", (string?)null, InstallationIds.Legacy), ("neighbor", "neighbor@example.test", Neighbor) })
                    {
                        var user = new IdentityUser { UserName = name, Email = email, EmailConfirmed = email is not null };
                        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
                        await using (var db = factory.CreateDbContext())
                        {
                            db.InstallationMemberships.Add(new() { UserId = user.Id, InstallationId = installation, Role = "Owner" }); await db.SaveChangesAsync();
                        }
                        Assert.True((await users.AddClaimAsync(user, new("preserved.preference", name))).Succeeded);
                        var session = await scope.ServiceProvider.GetRequiredService<MobileAuthService>().SignInAsync(new(name, Password)); Assert.NotNull(session);
                        if (installation == InstallationIds.Legacy) { host.OwnerId = user.Id; host.OwnerToken = session.Token; }
                        else { host.NeighborId = user.Id; host.NeighborToken = session.Token; }
                        await using var owned = factory.ForInstallation(installation);
                        owned.AppSettings.Add(new() { Section = "DeyeCloud", Key = "Password", Value = name + "-private" });
                        owned.Readings.Add(new() { Timestamp = clock.GetUtcNow().UtcDateTime, SolarDeviceSn = "same-inverter", SolarProduction = 3456 });
                        owned.ExportReadings.Add(new() { DeviceSn = "same-inverter", ObservedAt = clock.GetUtcNow().UtcDateTime, PolledAt = clock.GetUtcNow().UtcDateTime, GridPowerWatts = -1234 });
                        await owned.SaveChangesAsync();
                    }
                }
                return host;
            }
            catch
            {
                if (app is not null) await app.DisposeAsync(); await using var db = factory.CreateDbContext(); await db.Database.EnsureDeletedAsync(); throw;
            }
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose(); try { await app.StopAsync(); await app.DisposeAsync(); }
            finally { await using var db = Factory.CreateDbContext(); await db.Database.EnsureDeletedAsync(); }
        }
    }
    private sealed class Factory(DbContextOptions<DeyeSolarDbContext> options) : IDbContextFactory<DeyeSolarDbContext>
    {
        public DeyeSolarDbContext CreateDbContext() => new(options);
        public DeyeSolarDbContext ForInstallation(string id) => new(options, id);
        public Task<DeyeSolarDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
    // Only Google's external token/user-info network boundary is substituted; its OAuth middleware and all account HTTP/SQL paths are real.
    private sealed class GoogleBackchannel : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, object> profiles = new();
        public string Add(string subject, string email, bool verified)
        {
            var code = Guid.NewGuid().ToString("N"); profiles[code] = new { id = subject, sub = subject, email, verified_email = verified }; return code;
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host == "oauth2.googleapis.com" && request.Method == HttpMethod.Post)
            {
                var code = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(ct))["code"].ToString();
                Assert.True(profiles.ContainsKey(code));
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { access_token = code, token_type = "Bearer", expires_in = 3600 }) };
            }
            Assert.Equal("www.googleapis.com", request.RequestUri.Host); Assert.Equal(HttpMethod.Get, request.Method);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(profiles[request.Headers.Authorization!.Parameter!]) };
        }
    }
}
