using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Api;
using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Localization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DeyeSolar.Web.Tests;

public class AccountIdentitySqlServerTests
{
    private const string FixtureInstallation = "fixture-installation";
    private const string Password = "Local registration password 42!";
    private const string SiblingInstallation = "independent-installation";

    [SqlServerFact]
    public async Task IdentityStatusRequiresBearerAndOnlyReturnsTheSignedInAccountsVerifiedMethods()
    {
        await using var host = await Host.StartAsync();
        var first = await host.RegisterAsync("first-status@example.test");
        var second = await host.RegisterAsync("second-status@example.test");
        foreach (var token in new string?[] { null, "forged-bearer" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/identities");
            if (token is not null) request.Headers.Authorization = new("Bearer", token);
            using var denied = await host.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        foreach (var session in new[] { first, second })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/identities?userId=another-account");
            request.Headers.Authorization = new("Bearer", session.Token);
            using var response = await host.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var status = await response.Content.ReadFromJsonAsync<AccountIdentitiesResponse>();
            Assert.Equal(new AccountIdentitiesResponse(session.Username, null, false), status);
        }
    }

    [SqlServerFact]
    public async Task VerifiedEmailRegistrationCreatesSeparateEmptyInstallationsAndPreservesExistingData()
    {
        await using var host = await Host.StartAsync();
        var before = await host.ReadDataAsync();
        var first = await host.RegisterAsync("first@example.test");
        var second = await host.RegisterAsync("second@example.test");

        Assert.NotEqual(first.InstallationId, second.InstallationId);
        foreach (var session in new[] { first, second })
        {
            Assert.NotNull(session.InstallationId);
            Assert.NotEqual(FixtureInstallation, session.InstallationId);
            Assert.NotEqual(SiblingInstallation, session.InstallationId);
            await using var own = host.Factory.ForInstallation(session.InstallationId);
            Assert.Empty(await own.AppSettings.ToListAsync());
            Assert.Empty(await own.Readings.ToListAsync());
            Assert.Empty(await own.ExportReadings.ToListAsync());
            Assert.Empty(await own.TriggerRules.ToListAsync());
            Assert.Empty(await own.RuleRunLogs.ToListAsync());
            var user = await own.Users.SingleAsync(u => u.UserName == session.Username);
            Assert.True(user.EmailConfirmed);
            var membership = await own.InstallationMemberships.SingleAsync(m => m.UserId == user.Id);
            Assert.Equal(session.InstallationId, membership.InstallationId);
            Assert.Equal("Owner", membership.Role);
            Assert.Equal(64, session.Token.Length);

            // The returned bearer must pass the real user, security-stamp and membership checks.
            using var linkedProof = await host.StartResponseAsync("link-" + session.Username, "link", session.Token);
            Assert.Equal(HttpStatusCode.OK, linkedProof.StatusCode);
        }
        await using var check = host.Factory.CreateDbContext();
        Assert.Equal(4, await check.Users.CountAsync());
        Assert.Equal(4, await check.Installations.CountAsync());
        Assert.Equal(4, await check.InstallationMemberships.CountAsync());
        Assert.Equal(before, await host.ReadDataAsync());
    }

    [SqlServerFact]
    public async Task InvalidExpiredAndCrossPurposeProofsDoNotCreateAccountOrInstallation()
    {
        await using var host = await Host.StartAsync();
        var before = await host.ReadStateAsync();
        var proof = await host.StartAsync("wrong@example.test");
        var wrongCode = proof.Code == "000000" ? "000001" : "000000";
        using (var wrong = await host.CompleteAsync(proof with { Code = wrongCode }))
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        using (var shortPassword = await host.CompleteAsync(proof, "short"))
            Assert.Equal(HttpStatusCode.BadRequest, shortPassword.StatusCode);
        Assert.Equal(before, await host.ReadStateAsync());

        host.Clock.Advance(TimeSpan.FromMinutes(10));
        using (var expired = await host.CompleteAsync(proof))
            Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        var loginProof = await host.StartAsync("missing@example.test", "login");
        using (var wrongPurpose = await host.CompleteAsync(loginProof))
            Assert.Equal(HttpStatusCode.BadRequest, wrongPurpose.StatusCode);
        using (var unknownLogin = await host.Client.PostAsJsonAsync("/api/auth/verification/login",
            new VerificationCompleteRequest(loginProof.Id, loginProof.Code)))
            Assert.Equal(HttpStatusCode.Unauthorized, unknownLogin.StatusCode);

        host.Delivery.Fail = true;
        using (var deliveryFailed = await host.StartResponseAsync("unavailable@example.test"))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, deliveryFailed.StatusCode);
        Assert.Equal(before, await host.ReadStateAsync());
    }

    [SqlServerFact]
    public async Task ConcurrentAndRepeatedRegistrationProofCreatesExactlyOneOwner()
    {
        await using var host = await Host.StartAsync();
        var before = await host.ReadDataAsync();
        var proof = await host.StartAsync("concurrent@example.test");
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => host.CompleteAsync(proof)));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Equal(3, responses.Count(response => response.StatusCode == HttpStatusCode.BadRequest));
        }
        finally { foreach (var response in responses) response.Dispose(); }
        using (var replay = await host.CompleteAsync(proof))
            Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        await using var db = host.Factory.CreateDbContext();
        Assert.Equal(3, await db.Users.CountAsync());
        Assert.Equal(3, await db.Installations.CountAsync());
        Assert.Equal(3, await db.InstallationMemberships.CountAsync());
        var owner = await db.Users.SingleAsync(user => user.Email == "concurrent@example.test");
        Assert.Single(await db.InstallationMemberships.Where(m => m.UserId == owner.Id).ToListAsync());
        Assert.Equal(before, await host.ReadDataAsync());
    }

    [SqlServerFact]
    public async Task FailedMembershipWriteRollsBackIdentityAndInstallationAndRequiresNewProof()
    {
        await using var host = await Host.StartAsync();
        var before = await host.ReadStateAsync();
        var proof = await host.StartAsync("rollback@example.test");
        await using (var db = host.Factory.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE InstallationMemberships WITH NOCHECK
                ADD CONSTRAINT RejectRegistrationMembership CHECK (Role <> 'Owner');
                """);
        }
        using (var failed = await host.CompleteAsync(proof))
            Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
        Assert.Equal(before, await host.ReadStateAsync());

        await using (var db = host.Factory.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE InstallationMemberships DROP CONSTRAINT RejectRegistrationMembership;");
        using (var replay = await host.CompleteAsync(proof))
            Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal(before, await host.ReadStateAsync());
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        var session = await host.RegisterAsync("rollback@example.test");
        await using var check = host.Factory.CreateDbContext();
        Assert.Equal(3, await check.Users.CountAsync());
        Assert.Equal(3, await check.Installations.CountAsync());
        Assert.Equal(3, await check.InstallationMemberships.CountAsync());
        Assert.Equal(session.InstallationId, (await check.InstallationMemberships.SingleAsync(m =>
            m.User.Email == "rollback@example.test")).InstallationId);
    }

    [SqlServerFact]
    public async Task LinkingProofRequiresTheSameLiveAccountSessionAndSurvivesMembershipRemoval()
    {
        await using var host = await Host.StartAsync();
        var first = await host.RegisterAsync("first@example.test");
        var second = await host.RegisterAsync("second@example.test");
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        var proof = await host.StartAsync(first.Username, "link", first.Token);
        var before = await host.ReadStateAsync();
        foreach (var token in new string?[] { null, "forged-bearer", second.Token })
        {
            using var refused = await host.LinkAsync(proof, token);
            Assert.Equal(token == second.Token ? HttpStatusCode.BadRequest : HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.Equal(before, await host.ReadStateAsync());
        }
        using (var linked = await host.LinkAsync(proof, first.Token))
            Assert.Equal(HttpStatusCode.NoContent, linked.StatusCode);
        using (var replay = await host.LinkAsync(proof, first.Token))
            Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);

        await using (var db = host.Factory.CreateDbContext())
        {
            db.InstallationMemberships.Remove(await db.InstallationMemberships.SingleAsync(m => m.InstallationId == first.InstallationId));
            await db.SaveChangesAsync();
        }
        var revokedState = await host.ReadStateAsync();
        using (var removedMembership = await host.StartResponseAsync("removed-membership@example.test", "link", first.Token))
            Assert.Equal(HttpStatusCode.OK, removedMembership.StatusCode);
        Assert.Equal(revokedState, await host.ReadStateAsync());
        await host.RevokeSessionAsync(first.Token);
        using (var revoked = await host.StartResponseAsync("revoked@example.test", "link", first.Token))
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.Equal(revokedState, await host.ReadStateAsync());
        using var independent = await host.StartResponseAsync("independent@example.test", "link", second.Token);
        Assert.Equal(HttpStatusCode.OK, independent.StatusCode);
    }

    [SqlServerFact]
    public async Task LanguagePreferencesUseAuthenticatedUsersAndPreserveSharedInstallationData()
    {
        await using var host = await Host.StartAsync();
        var first = await host.RegisterAsync("first-language@example.test");
        var second = await host.RegisterAsync("second-language@example.test");
        second = await host.JoinInstallationAsync(second, first.InstallationId!);
        Assert.Equal(first.InstallationId, second.InstallationId);
        var before = await host.ReadDataAsync();
        var identitiesBefore = await host.ReadIdentitiesAsync();
        string secondUserId;
        await using (var lookup = host.Factory.CreateDbContext())
            secondUserId = (await lookup.Users.SingleAsync(user => user.UserName == second.Username)).Id;
        using (var saved = await host.LanguageAsync(HttpMethod.Put, first.Token, new { language = "ru" }, "ru-RU"))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.Equal("ru", Assert.Single(saved.Content.Headers.ContentLanguage));
        }
        using (var saved = await host.LanguageAsync(HttpMethod.Put, second.Token, new { language = "pl" }, "pl-PL"))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using (var own = await host.LanguageAsync(HttpMethod.Put, first.Token,
            new { language = "de", userId = secondUserId }, "tr-TR"))
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        foreach (var invalid in new string?[] { null, "", "RU", "ru-RU", "xx" })
        {
            using var denied = await host.LanguageAsync(HttpMethod.Put, first.Token, new { language = invalid });
            Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        }
        foreach (var token in new string?[] { null, "forged-bearer" })
        {
            using var denied = await host.LanguageAsync(HttpMethod.Put, token, new { language = "ja" });
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        foreach (var (session, expected) in new[] { (first, "de"), (second, "pl") })
        {
            using var read = await host.LanguageAsync(HttpMethod.Get, session.Token, header: "unsupported");
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal("en", Assert.Single(read.Content.Headers.ContentLanguage));
            Assert.Equal(expected, (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("language").GetString());
        }

        // Recreate all preference services against the same SQL database, rather than relying on a request-scope cache.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => host.Factory.CreateDbContext());
        services.AddIdentity<IdentityUser, IdentityRole>().AddEntityFrameworkStores<DeyeSolarDbContext>();
        services.AddScoped<UserLanguageService>();
        await using var restarted = services.BuildServiceProvider();
        using var scope = restarted.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var preferences = scope.ServiceProvider.GetRequiredService<UserLanguageService>();
        var firstUser = (await users.FindByNameAsync(first.Username))!;
        var secondUser = (await users.FindByNameAsync(second.Username))!;
        Assert.Equal("de", await preferences.GetAsync(firstUser.Id));
        Assert.Equal("pl", await preferences.GetAsync(secondUser.Id));
        await using var db = host.Factory.CreateDbContext();
        Assert.Equal(2, await db.UserClaims.CountAsync(claim => claim.ClaimType == UserLanguageService.ClaimType));
        Assert.Equal(2, await db.InstallationMemberships.CountAsync(m => m.InstallationId == first.InstallationId));
        Assert.Equal(before, await host.ReadDataAsync());
        Assert.Equal(identitiesBefore, await host.ReadIdentitiesAsync());
    }

    [SqlServerFact]
    public async Task ConcurrentRequestLanguagesRespectBrowserCookieAndBearerHeaderWithoutCrossRequestLeakage()
    {
        await using var host = await Host.StartAsync();
        var session = await host.RegisterAsync("request-language@example.test");
        var before = await host.ReadStateAsync();
        var requests = new[]
        {
            (Header: "fr;q=0.2,pl-PL;q=0.9", Cookie: (string?)null, Token: (string?)null, Expected: "pl"),
            (Header: "fr", Cookie: "uk", Token: (string?)null, Expected: "uk"),
            (Header: "de", Cookie: "unsupported", Token: (string?)null, Expected: "de"),
            (Header: "ja", Cookie: "ru", Token: (string?)session.Token, Expected: "ja"),
            (Header: "unsupported", Cookie: (string?)null, Token: (string?)null, Expected: "en")
        };
        await Task.WhenAll(requests.Select(async item =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/options");
            request.Headers.TryAddWithoutValidation("Accept-Language", item.Header);
            if (item.Cookie is not null) request.Headers.Add("Cookie", "solar.language=" + item.Cookie);
            if (item.Token is not null) request.Headers.Authorization = new("Bearer", item.Token);
            using var response = await host.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(item.Expected, Assert.Single(response.Content.Headers.ContentLanguage));
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(body.GetProperty("registrationEnabled").GetBoolean());
            Assert.True(body.GetProperty("emailEnabled").GetBoolean());
            Assert.False(body.GetProperty("googleEnabled").GetBoolean());
        }));
        Assert.Equal(before, await host.ReadStateAsync());
    }

    private sealed record Proof(string Id, string Code);

    private sealed class Host(WebApplication app, HttpClient client, Factory factory, Clock clock, Delivery delivery, SqlServerTestDatabase database) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public Factory Factory { get; } = factory;
        public Clock Clock { get; } = clock;
        public Delivery Delivery { get; } = delivery;
        public Task RevokeSessionAsync(string token) => app.Services.GetRequiredService<IAccountSessionStore>().RevokeAsync(token);

        public async Task<HttpResponseMessage> LanguageAsync(HttpMethod method, string? token,
            object? body = null, string header = "en")
        {
            using var request = new HttpRequestMessage(method, "/api/account/language");
            if (body is not null) request.Content = JsonContent.Create(body);
            if (token is not null) request.Headers.Authorization = new("Bearer", token);
            request.Headers.TryAddWithoutValidation("Accept-Language", header);
            return await Client.SendAsync(request);
        }

        public async Task<MobileAuthResponse> JoinInstallationAsync(MobileAuthResponse session, string installationId)
        {
            await using (var db = Factory.CreateDbContext())
            {
                var userId = (await db.Users.SingleAsync(user => user.UserName == session.Username)).Id;
                db.InstallationMemberships.Remove(await db.InstallationMemberships.SingleAsync(m => m.UserId == userId));
                db.InstallationMemberships.Add(new() { UserId = userId, InstallationId = installationId, Role = "Owner" });
                await db.SaveChangesAsync();
            }
            using var scope = app.Services.CreateScope();
            var user = (await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().FindByNameAsync(session.Username))!;
            return (await scope.ServiceProvider.GetRequiredService<AccountIdentityService>().SessionAsync(user.Id, default))!;
        }

        public Task<HttpResponseMessage> StartResponseAsync(string email, string purpose = "register", string? token = null)
            => SendAsync("/api/auth/verification/start", new VerificationStartRequest("email", email, purpose), token);

        public async Task<Proof> StartAsync(string email, string purpose = "register", string? token = null)
        {
            using var response = await StartResponseAsync(email, purpose, token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var challenge = (await response.Content.ReadFromJsonAsync<VerificationStartResponse>())!;
            return new(challenge.VerificationId, Delivery.Codes[email.ToLowerInvariant()]);
        }

        public Task<HttpResponseMessage> CompleteAsync(Proof proof, string password = Password)
            => Client.PostAsJsonAsync("/api/auth/register", new AccountRegisterRequest(proof.Id, proof.Code, password));

        public Task<HttpResponseMessage> LinkAsync(Proof proof, string? token)
            => SendAsync("/api/auth/identities/link", new VerificationCompleteRequest(proof.Id, proof.Code), token);

        private async Task<HttpResponseMessage> SendAsync<T>(string path, T body, string? token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            if (token is not null) request.Headers.Authorization = new("Bearer", token);
            return await Client.SendAsync(request);
        }

        public async Task<MobileAuthResponse> RegisterAsync(string email)
        {
            var proof = await StartAsync(email);
            using var response = await CompleteAsync(proof);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<MobileAuthResponse>())!;
        }

        public async Task<string> ReadDataAsync()
        {
            await using var db = Factory.CreateDbContext();
            return JsonSerializer.Serialize(new
            {
                Settings = await db.AppSettings.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id).ToListAsync(),
                Readings = await db.Readings.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id).ToListAsync(),
                Exports = await db.ExportReadings.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.InstallationId).ThenBy(row => row.ObservedAt).ToListAsync(),
                Rules = await db.TriggerRules.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id).ToListAsync(),
                Logs = await db.RuleRunLogs.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id).ToListAsync()
            });
        }

        public async Task<string> ReadStateAsync()
        {
            await using var db = Factory.CreateDbContext();
            return JsonSerializer.Serialize(new
            {
                Data = await ReadDataAsync(),
                Users = await db.Users.AsNoTracking().OrderBy(user => user.Id).ToListAsync(),
                Installations = await db.Installations.AsNoTracking().OrderBy(installation => installation.Id).ToListAsync(),
                Memberships = await db.InstallationMemberships.AsNoTracking().OrderBy(m => m.UserId).ThenBy(m => m.InstallationId)
                    .Select(m => new { m.UserId, m.InstallationId, m.Role }).ToListAsync()
            });
        }

        public async Task<string> ReadIdentitiesAsync()
        {
            await using var db = Factory.CreateDbContext();
            return JsonSerializer.Serialize(new
            {
                Users = await db.Users.AsNoTracking().OrderBy(user => user.Id).Select(user => new
                {
                    user.Id,
                    user.UserName,
                    user.NormalizedUserName,
                    user.Email,
                    user.NormalizedEmail,
                    user.EmailConfirmed,
                    user.PhoneNumber,
                    user.PhoneNumberConfirmed,
                    user.PasswordHash,
                    user.SecurityStamp
                }).ToArrayAsync(),
                Memberships = await db.InstallationMemberships.AsNoTracking().OrderBy(row => row.UserId)
                    .ThenBy(row => row.InstallationId).Select(row => new { row.UserId, row.InstallationId, row.Role }).ToArrayAsync()
            });
        }

        public static async Task<Host> StartAsync()
        {
            var database = await SqlServerTestDatabase.CreateAsync("SolarAccountIdentityTests");
            var factory = new Factory(database.Options);
            WebApplication? app = null;
            try
            {
                await using (var db = factory.CreateDbContext())
                {
                    db.Installations.AddRange(new Installation { Id = FixtureInstallation, CreatedAt = DateTimeOffset.UtcNow }, new Installation { Id = SiblingInstallation, CreatedAt = DateTimeOffset.UtcNow });
                    foreach (var installationId in new[] { FixtureInstallation, SiblingInstallation })
                    {
                        var user = new IdentityUser
                        {
                            UserName = installationId,
                            NormalizedUserName = installationId.ToUpperInvariant(),
                            Email = installationId + "@example.test",
                            NormalizedEmail = installationId.ToUpperInvariant() + "@EXAMPLE.TEST",
                            EmailConfirmed = true,
                            SecurityStamp = Guid.NewGuid().ToString()
                        };
                        db.Users.Add(user);
                        db.InstallationMemberships.Add(new InstallationMembership { UserId = user.Id, InstallationId = installationId });
                    }
                    await db.SaveChangesAsync();
                }
                foreach (var installationId in new[] { FixtureInstallation, SiblingInstallation })
                {
                    await using var db = factory.ForInstallation(installationId);
                    db.AppSettings.Add(new AppSetting { Section = "DeyeCloud", Key = "Password", Value = installationId + "-private" });
                    db.Readings.Add(new Reading { Timestamp = DateTime.UtcNow, SolarDeviceSn = "same-inverter", SolarProduction = 3456 });
                    db.ExportReadings.Add(new() { DeviceSn = "same-inverter", ObservedAt = DateTime.UtcNow, PolledAt = DateTime.UtcNow, GridPowerWatts = -1234 });
                    db.TriggerRules.Add(new() { Name = installationId + " rule", EntityId = "same-socket", SocTurnOnThreshold = 50 });
                    await db.SaveChangesAsync();
                }

                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = AppContext.BaseDirectory });
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Logging.ClearProviders();
                builder.Services.AddSingleton<IDbContextFactory<DeyeSolarDbContext>>(factory);
                builder.Services.AddScoped(_ => factory.CreateDbContext());
                builder.Services.AddIdentity<IdentityUser, IdentityRole>(options =>
                {
                    options.Password.RequiredLength = 12;
                    options.Password.RequireDigit = false;
                    options.Password.RequireLowercase = false;
                    options.Password.RequireUppercase = false;
                    options.Password.RequireNonAlphanumeric = false;
                }).AddEntityFrameworkStores<DeyeSolarDbContext>();
                builder.Services.AddAccountIdentities(new AuthProviderOptions
                {
                    RegistrationEnabled = true,
                    ResendApiKey = "local-test-key",
                    EmailFrom = "Local test <local@example.test>"
                });
                builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, MobileBearerAuthenticationHandler>(
                    MobileBearerAuthenticationHandler.SchemeName, _ => { });
                builder.Services.AddAuthorization();
                builder.Services.AddHttpContextAccessor();
                builder.Services.AddAntiforgery();
                builder.Services.AddScoped<UiText>();
                builder.Services.AddScoped<UserLanguageService>();
                builder.Services.AddSingleton<MobileSessionStore>();
            builder.Services.AddSingleton<DeyeSolar.Web.Auth.IAccountSessionStore>(p => p.GetRequiredService<MobileSessionStore>());
                var clock = new Clock();
                var delivery = new Delivery();
                builder.Services.AddSingleton<TimeProvider>(clock);
                builder.Services.AddSingleton<IIdentityVerificationDelivery>(delivery);
                app = builder.Build();
                app.UseRouting();
                app.UseRateLimiter();
                app.UseAuthentication();
                app.UseMiddleware<LanguageMiddleware>();
                app.UseAuthorization();
                app.MapAccountIdentityApi();
                app.MapUserLanguage();
                await app.StartAsync();
                var address = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses));
                Assert.Equal("127.0.0.1", address.Host);
                var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
                { BaseAddress = address, Timeout = TimeSpan.FromSeconds(20) };
                return new(app, client, factory, clock, delivery, database);
            }
            catch
            {
                await TestHttpHostCleanup.DisposeAsync(app, database);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await TestHttpHostCleanup.DisposeAsync(app, database, stop: true);
        }
    }

    private sealed class Factory(DbContextOptions<DeyeSolarDbContext> options) : IDbContextFactory<DeyeSolarDbContext>
    {
        public DeyeSolarDbContext CreateDbContext() => new(options);
        public DeyeSolarDbContext ForInstallation(string installationId) => new(options, installationId);
        public Task<DeyeSolarDbContext> CreateDbContextAsync(CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(CreateDbContext()); }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    // Delivery is captured in memory; these tests never contact Resend or send email.
    private sealed class Delivery : IIdentityVerificationDelivery
    {
        public ConcurrentDictionary<string, string> Codes { get; } = new();
        public bool Fail { get; set; }
        public Task SendEmailAsync(string destination, string code, CancellationToken ct)
        {
            if (Fail) throw new VerificationDeliveryException();
            Codes[destination] = code;
            return Task.CompletedTask;
        }
        public Task SendPhoneAsync(string destination, CancellationToken ct) => throw new InvalidOperationException("SMS is not enabled in this fixture.");
        public Task<bool> CheckPhoneAsync(string destination, string code, CancellationToken ct) => throw new InvalidOperationException("SMS is not enabled in this fixture.");
    }
}
