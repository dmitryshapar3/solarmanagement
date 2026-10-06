using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DeyeSolar.Web.Api;
using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DeyeSolar.Web.Tests;

public class GoogleIdentityFlowTests
{
    private const string Verifier = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static string Challenge => WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier)));
    private const string State = "local_test_state_1234567890";
    private sealed class SqliteModelContext(DbContextOptions<DeyeSolarDbContext> options) : DeyeSolarDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Installation>().Property(i => i.CreatedAt).HasConversion<long>();
            // SQLite needs scalar storage for the live Identity lockout comparison; SQL Server
            // uses its native datetimeoffset column in production.
            modelBuilder.Entity<IdentityUser>().Property(u => u.LockoutEnd).HasConversion<long>();
        }
    }
    private sealed class Factory(DbContextOptions<DeyeSolarDbContext> options) : IDbContextFactory<DeyeSolarDbContext>
    {
        public DeyeSolarDbContext CreateDbContext() => new(options);
        public Task<DeyeSolarDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
    private sealed class Host : IAsyncDisposable
    {
        public readonly SqliteConnection Connection = new("Data Source=:memory:");
        public WebApplication App = null!; public HttpClient Client = null!;
        public DbContextOptions<DeyeSolarDbContext> Options = null!;
        public string OwnerA = ""; public string OwnerB = ""; public string TokenA = ""; public string TokenB = "";
        public async Task InitializeAsync()
        {
            await Connection.OpenAsync(); Connection.CreateCollation("Latin1_General_100_BIN2", string.CompareOrdinal);
            Options = new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlite(Connection).Options;
            await using (var model = new SqliteModelContext(Options))
            { await model.Database.EnsureCreatedAsync(); Options = new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlite(Connection).UseModel(model.Model).Options; }
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = AppContext.BaseDirectory });
            builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton<IDbContextFactory<DeyeSolarDbContext>>(new Factory(Options)); builder.Services.AddScoped(_ => new DeyeSolarDbContext(Options));
            builder.Services.AddIdentity<IdentityUser, IdentityRole>().AddEntityFrameworkStores<DeyeSolarDbContext>();
            builder.Services.ConfigureApplicationCookie(options => options.Cookie.SecurePolicy = CookieSecurePolicy.Always);
            builder.Services.AddAccountIdentities(new AuthProviderOptions { GoogleClientId = "local-test-client", GoogleClientSecret = "local-test-secret" });
            builder.Services.AddSingleton<TimeProvider>(TimeProvider.System); builder.Services.AddSingleton<MobileSessionStore>();
            builder.Services.AddSingleton<DeyeSolar.Web.Auth.IAccountSessionStore>(p => p.GetRequiredService<MobileSessionStore>());
            builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, MobileBearerAuthenticationHandler>(MobileBearerAuthenticationHandler.SchemeName, _ => { });
            builder.Services.AddAuthorization(); App = builder.Build();
            App.UseRouting(); App.UseRateLimiter(); App.UseAccountIdentityOrigin(); App.UseAuthentication(); App.UseAuthorization();
            App.MapAccountIdentityApi(); App.MapGoogleIdentity();
            // Local-only synthetic provider proof. This endpoint exists solely inside this test application.
            App.MapGet("/test/external", async (HttpContext context, GoogleMobileTicketStore tickets) =>
            {
                var properties = new AuthenticationProperties(); properties.Items["solar.oauth.once"] = tickets.StartCallback();
                var owner = context.Request.Query["owner"].ToString(); if (owner.Length != 0) properties.Items["solar.link.user"] = owner;
                if (context.Request.Query["mobile"] == "true") { properties.Items["solar.mobile.challenge"] = Challenge; properties.Items["solar.mobile.state"] = State; }
                var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "verified-google-subject"),
                    new Claim(ClaimTypes.Email, "google@example.test"), new Claim("google:email_verified", "true")], "test-external"));
                await context.SignInAsync(IdentityConstants.ExternalScheme, principal, properties); return Results.NoContent();
            });
            App.MapGet("/test/cookie", async (HttpContext context, SignInManager<IdentityUser> signIn) =>
            { var user = await signIn.UserManager.FindByIdAsync(context.Request.Query["owner"].ToString()); await signIn.SignInAsync(user!, true); return Results.NoContent(); });
            using (var scope = App.Services.CreateScope())
            {
                var accounts = scope.ServiceProvider.GetRequiredService<AccountIdentityService>();
                var a = await accounts.RegisterAsync(new("email", "owner-a@example.test"), "Long-local-test-password42!", default);
                var b = await accounts.RegisterAsync(new("email", "owner-b@example.test"), "Long-local-test-password42!", default);
                OwnerA = a.Id; OwnerB = b.Id; TokenA = (await accounts.SessionAsync(a.Id, default))!.Token; TokenB = (await accounts.SessionAsync(b.Id, default))!.Token;
            }
            await App.StartAsync(); var address = Assert.Single(App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
            Client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new(address) };
        }
        public async Task<string> ExternalAsync(string owner, bool mobile)
        { using var response = await Client.GetAsync($"/test/external?owner={owner}&mobile={mobile.ToString().ToLowerInvariant()}"); return Cookies(response); }
        public async Task<string> CookieAsync(string owner)
        { using var response = await Client.GetAsync($"/test/cookie?owner={owner}"); Assert.Contains(response.Headers.GetValues("Set-Cookie"), cookie => cookie.Contains("secure", StringComparison.OrdinalIgnoreCase)); return Cookies(response); }
        private static string Cookies(HttpResponseMessage response) => string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0]));
        public async Task<HttpResponseMessage> CompleteAsync(string cookies)
        { using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/google/complete"); request.Headers.Add("Cookie", cookies); return await Client.SendAsync(request); }
        public async Task<HttpResponseMessage> ExchangeAsync(string code, string verifier, string? token)
        { using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/google/exchange") { Content = JsonContent.Create(new GoogleMobileExchangeRequest(code, verifier)) };
          if (token is not null) request.Headers.Authorization = new("Bearer", token); return await Client.SendAsync(request); }
        public async Task<int> LoginCountAsync() { await using var db = new DeyeSolarDbContext(Options); return await db.UserLogins.CountAsync(); }
        public async ValueTask DisposeAsync() { Client?.Dispose(); if (App is not null) await App.DisposeAsync(); await Connection.DisposeAsync(); }
    }

    [Fact]
    public async Task MobileCallbackHasNoLinkSideEffectUntilSameBearerAndPkceExchange()
    {
        await using var host = new Host(); await host.InitializeAsync(); var cookie = await host.ExternalAsync(host.OwnerA, true);
        using var callback = await host.CompleteAsync(cookie); Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        var location = callback.Headers.Location!; Assert.Equal("deyesolar", location.Scheme); Assert.Equal("auth", location.Host); Assert.Equal("/callback", location.AbsolutePath);
        var query = QueryHelpers.ParseQuery(location.Query); Assert.Equal(State, query["state"].ToString()); var code = query["code"].ToString();
        Assert.Equal(0, await host.LoginCountAsync());
        foreach (var token in new string?[] { null, host.TokenB })
        { using var refused = await host.ExchangeAsync(code, Verifier, token); Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode); Assert.Equal(0, await host.LoginCountAsync()); }
        using (var wrong = await host.ExchangeAsync(code, new string('b', 64), host.TokenA)) Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        using (var success = await host.ExchangeAsync(code, Verifier, host.TokenA)) Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.Equal(1, await host.LoginCountAsync()); await using var db = new DeyeSolarDbContext(host.Options);
        Assert.Equal(host.OwnerA, (await db.UserLogins.SingleAsync()).UserId);
        using var replay = await host.ExchangeAsync(code, Verifier, host.TokenA); Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task WebLinkCallbackRequiresOriginalSignedInAccount()
    {
        await using var host = new Host(); await host.InitializeAsync(); var external = await host.ExternalAsync(host.OwnerA, false);
        var wrong = await host.CookieAsync(host.OwnerB);
        using (var refused = await host.CompleteAsync(external + "; " + wrong)) Assert.Contains("error=link_failed", refused.Headers.Location!.ToString());
        Assert.Equal(0, await host.LoginCountAsync()); external = await host.ExternalAsync(host.OwnerA, false);
        var right = await host.CookieAsync(host.OwnerA);
        using (var success = await host.CompleteAsync(external + "; " + right)) Assert.Equal("/settings/account?linked=google", success.Headers.Location!.ToString());
        Assert.Equal(1, await host.LoginCountAsync());
    }

    [Fact]
    public async Task GoogleCallbackUsesConfiguredHttpsOriginAndCookieCannotUseJsonLinkApi()
    {
        await using var host = new Host(); await host.InitializeAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/auth/google?mobile=true&codeChallenge={Challenge}&state={State}");
        request.Headers.Host = "attacker.example"; request.Headers.Add("X-Forwarded-Host", "attacker.example"); request.Headers.Add("X-Forwarded-Proto", "http");
        using var challenge = await host.Client.SendAsync(request); Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        Assert.Equal("https://solar.dshapar.com/signin-google", QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["redirect_uri"].ToString());
        var cookie = await host.CookieAsync(host.OwnerA);
        using var linkRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/google/link/start") { Content = JsonContent.Create(new GoogleMobileLinkStartRequest(Challenge, State)) };
        linkRequest.Headers.Add("Cookie", cookie); using var refused = await host.Client.SendAsync(linkRequest); Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }
}
