using DeyeSolar.Web.Auth;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using DeyeSolar.Web.Api;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Integrations;
using DeyeSolar.Web.Tenancy;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SolarManagement.Integrations.Contracts;
using SolarManagement.Integrations.Runtime;

namespace DeyeSolar.Web.Tests;

public class IntegrationOAuthSqlServerTests
{
    [SqlServerFact]
    public async Task RealBearerFlowKeepsTokensEncryptedUntilExplicitSaveAndCannotBeReplayed()
    {
        await using var f = await Fixture.CreateAsync();
        var neighbour = await f.PrivateStateAsync("b");
        var before = await f.PrivateStateAsync("a");
        var start = await f.StartAsync();
        Assert.Equal(IntegrationOAuthOptions.MobileReturnUri, start.ReturnUri);
        Assert.Equal(43, f.Executor.Begin!.CodeChallenge.Length);
        Assert.Equal("S256", f.Executor.Begin.CodeChallengeMethod);
        await using (var db = f.Factory("a").CreateDbContext())
        {
            var stored = await db.IntegrationOAuthFlows.SingleAsync();
            Assert.DoesNotContain(f.Executor.Begin.State, stored.StateHash);
            Assert.DoesNotContain(f.Token, stored.Ciphertext);
        }
        using var callback = await f.CallbackAsync();
        Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
        Assert.Equal($"{start.ReturnUri}?flowId={start.FlowId:D}&returnNonce={start.ReturnNonce}", callback.Headers.Location!.OriginalString);
        using var replay = await f.CallbackAsync();
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal(1, f.Executor.CompleteCalls);
        var status = await f.StatusAsync(start.FlowId);
        Assert.Equal("ready", status.Status);
        Assert.True(status.SecretPresent["accessToken"]);
        Assert.DoesNotContain("provider-access-token", JsonSerializer.Serialize(status));
        Assert.Equal(before, await f.PrivateStateAsync("a"));
        var ready = f.Draft with { OAuthFlowId = start.FlowId };
        using var probe = await f.Client.PostAsJsonAsync($"/api/v2/integrations/{f.Instance.Id}/test", ready);
        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
        Assert.Equal(before, await f.PrivateStateAsync("a"));
        using var saved = await f.Client.PutAsJsonAsync($"/api/v2/integrations/{f.Instance.Id}/configuration", ready);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var config = (await saved.Content.ReadFromJsonAsync<IntegrationConfigurationDto>())!;
        Assert.Equal(f.Instance.Revision + 1, config.Instance.Revision);
        Assert.True(config.SecretPresent["accessToken"]);
        Assert.DoesNotContain("provider-access-token", await saved.Content.ReadAsStringAsync());
        await using (var db = f.Factory("a").CreateDbContext())
        {
            var row = await db.IntegrationConfigurations.SingleAsync(c => c.InstanceId == f.Instance.Id && c.Revision == config.Instance.Revision);
            Assert.DoesNotContain("provider-access-token", row.SecretsCiphertext);
            Assert.Equal("provider-access-token", f.Secrets.Decrypt("a", f.Instance.Id, row.Revision, row.SecretsCiphertext)["accessToken"]);
            var flow = await db.IntegrationOAuthFlows.SingleAsync();
            Assert.Equal("consumed", flow.Status);
            Assert.Empty(flow.Ciphertext);
        }
        var after = await f.PrivateStateAsync("a");
        using var reused = await f.Client.PutAsJsonAsync($"/api/v2/integrations/{f.Instance.Id}/configuration", ready with { ExpectedRevision = config.Instance.Revision });
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        Assert.Equal(after, await f.PrivateStateAsync("a"));
        Assert.Equal(neighbour, await f.PrivateStateAsync("b"));
    }

    [SqlServerFact]
    public async Task ForeignIdentityAndCookieCsrfCannotStartReadCancelOrCompleteAnotherAccountsFlow()
    {
        await using var f = await Fixture.CreateAsync();
        var start = await f.StartAsync();
        var before = await f.PrivateStateAsync("a");
        var foreignToken = await f.LoginAsync("b", false);
        f.Client.DefaultRequestHeaders.Authorization = new("Bearer", foreignToken);
        using (var read = await f.Client.GetAsync($"/api/v2/integrations/{f.Instance.Id}/oauth/{start.FlowId}")) Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        using (var cancel = await f.Client.PostAsync($"/api/v2/integrations/{f.Instance.Id}/oauth/{start.FlowId}/cancel", null)) Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
        using (var begin = await f.Client.PostAsJsonAsync($"/api/v2/integrations/{f.Instance.Id}/oauth/start", new IntegrationOAuthStartRequest(f.Draft, "mobile"))) Assert.Equal(HttpStatusCode.NotFound, begin.StatusCode);
        f.Client.DefaultRequestHeaders.Authorization = null;
        await f.LoginAsync("a", true);
        using (var noCsrf = await f.Client.PostAsJsonAsync($"/api/v2/integrations/{f.Instance.Id}/oauth/start", new IntegrationOAuthStartRequest(f.Draft))) Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        Assert.Equal(before, await f.PrivateStateAsync("a"));
        Assert.Equal(0, f.Executor.CompleteCalls);
    }

    [SqlServerFact]
    public async Task ConcurrentCallbacksExchangeTheCodeOnceAndCancellationFencesTheLateResult()
    {
        await using var f = await Fixture.CreateAsync();
        var before = await f.PrivateStateAsync("a");
        var start = await f.StartAsync();
        f.Executor.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Executor.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = f.CallbackAsync();
        await f.Executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using (var second = await f.CallbackAsync()) Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        using (var cancel = await f.Client.PostAsync($"/api/v2/integrations/{f.Instance.Id}/oauth/{start.FlowId}/cancel", null)) Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        f.Executor.Release.TrySetResult();
        using (var finished = await first) Assert.Equal(HttpStatusCode.Found, finished.StatusCode);
        Assert.Equal(1, f.Executor.CompleteCalls);
        Assert.Equal("cancelled", (await f.StatusAsync(start.FlowId)).Status);
        Assert.Equal(before, await f.PrivateStateAsync("a"));
        await using var db = f.Factory("a").CreateDbContext();
        Assert.Empty((await db.IntegrationOAuthFlows.SingleAsync()).Ciphertext);
    }

    [SqlServerFact]
    public async Task ChangedConfigurationAndDraftCannotApplyLateOrReadyAuthorization()
    {
        await using var f = await Fixture.CreateAsync();
        var start = await f.StartAsync();
        using (var callback = await f.CallbackAsync()) Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
        var before = await f.PrivateStateAsync("a");
        var changed = f.Draft with { OAuthFlowId = start.FlowId, Values = new() { ["region"] = IntegrationJson.Element("us") } };
        using (var denied = await f.Client.PutAsJsonAsync($"/api/v2/integrations/{f.Instance.Id}/configuration", changed)) Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        Assert.Equal(before, await f.PrivateStateAsync("a"));
        using (var manual = await f.Client.PutAsJsonAsync($"/api/v2/integrations/{f.Instance.Id}/configuration", f.Draft with
        { SecretOperations = new() { ["accessToken"] = new("replace", "manual-new-token") } })) Assert.Equal(HttpStatusCode.OK, manual.StatusCode);
        var after = await f.PrivateStateAsync("a");
        Assert.Equal("failed", (await f.StatusAsync(start.FlowId)).Status);
        using (var denied = await f.Client.PutAsJsonAsync($"/api/v2/integrations/{f.Instance.Id}/configuration", f.Draft with { OAuthFlowId = start.FlowId })) Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        Assert.Equal(after, await f.PrivateStateAsync("a"));
    }

    [SqlServerFact]
    public async Task RevokedMobileSessionAndExpiredStateFailBeforeProviderExchange()
    {
        await using var f = await Fixture.CreateAsync();
        var start = await f.StartAsync();
        var before = await f.PrivateStateAsync("a");
        f.App.Services.GetRequiredService<MobileSessionStore>().Revoke(f.Token);
        using (var revoked = await f.CallbackAsync()) Assert.Equal(HttpStatusCode.BadRequest, revoked.StatusCode);
        Assert.Equal(0, f.Executor.CompleteCalls);
        f.Token = await f.LoginAsync("a", false);
        f.Client.DefaultRequestHeaders.Authorization = new("Bearer", f.Token);
        var second = await f.StartAsync();
        await using (var db = f.Factory("a").CreateDbContext())
        {
            (await db.IntegrationOAuthFlows.SingleAsync(s => s.Id == second.FlowId)).ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        using (var expired = await f.CallbackAsync()) Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        Assert.Equal("expired", (await f.StatusAsync(second.FlowId)).Status);
        Assert.Equal(0, f.Executor.CompleteCalls);
        Assert.Equal(before, await f.PrivateStateAsync("a"));
    }

    [SqlServerFact]
    public async Task DeniedAndMalformedCallbacksDoNotExchangeTokensOrChangeConfiguration()
    {
        await using var f = await Fixture.CreateAsync();
        var start = await f.StartAsync();
        var before = await f.PrivateStateAsync("a");
        using (var noDraft = await f.Client.PostAsJsonAsync($"/api/v2/integrations/{f.Instance.Id}/oauth/start", new { draft = (object?)null, client = "mobile" })) Assert.Equal(HttpStatusCode.BadRequest, noDraft.StatusCode);
        using (var missingFields = await f.Client.PostAsJsonAsync($"/api/v2/integrations/{f.Instance.Id}/test", f.Draft with { OAuthFlowId = start.FlowId, SecretOperations = null! })) Assert.Equal(HttpStatusCode.BadRequest, missingFields.StatusCode);
        using (var duplicate = await f.Anonymous.GetAsync($"/integrations/oauth/callback?state={f.Executor.Begin!.State}&state=other&code=approved")) Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        using (var denied = await f.Anonymous.GetAsync($"/integrations/oauth/callback?state={f.Executor.Begin!.State}&error=access_denied")) Assert.Equal(HttpStatusCode.Found, denied.StatusCode);
        Assert.Equal("failed", (await f.StatusAsync(start.FlowId)).Status);
        Assert.Equal(0, f.Executor.CompleteCalls);
        Assert.Equal(before, await f.PrivateStateAsync("a"));
    }

    [SqlServerFact]
    public async Task BoundForeignOAuthAccountCannotReplaceCredentialsAndOperatorOriginsRequirePersistedRole()
    {
        await using var f = await Fixture.CreateAsync();
        await using (var db = f.Factory("a").CreateDbContext())
        {
            (await db.IntegrationInstances.SingleAsync()).AccountIdentity = "account-a";
            await db.SaveChangesAsync();
        }
        f.Executor.AccountIdentity = "foreign-account";
        var before = await f.PrivateStateAsync("a");
        var neighbour = await f.PrivateStateAsync("b");
        var start = await f.StartAsync();
        using (var callback = await f.CallbackAsync()) Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
        Assert.Equal("failed", (await f.StatusAsync(start.FlowId)).Status);
        using (var denied = await f.Client.PutAsJsonAsync($"/api/v2/integrations/{f.Instance.Id}/configuration", f.Draft with { OAuthFlowId = start.FlowId })) Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        using (var denied = await f.Client.PostAsJsonAsync("/api/v2/integration-packages/approved-origins", new IntegrationOriginApprovalRequest("https://provider.example"))) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Empty(f.OriginManager.Approved);
        using (var scope = f.App.Services.CreateScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            Assert.True((await roles.CreateAsync(new IdentityRole("PlatformOperator"))).Succeeded);
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            Assert.True((await users.AddToRoleAsync((await users.FindByIdAsync("a"))!, "PlatformOperator")).Succeeded);
        }
        using (var approved = await f.Client.PostAsJsonAsync("/api/v2/integration-packages/approved-origins", new IntegrationOriginApprovalRequest("https://provider.example"))) Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal("https://provider.example", Assert.Single(f.OriginManager.Approved));
        Assert.Equal(before, await f.PrivateStateAsync("a"));
        Assert.Equal(neighbour, await f.PrivateStateAsync("b"));
    }

    private sealed class Fixture(DbContextOptions<DeyeSolarDbContext> options) : IAsyncDisposable
    {
        public Executor Executor { get; } = new();
        public OriginManager OriginManager { get; } = new();
        public IntegrationSecretStore Secrets { get; } = new(new EphemeralDataProtectionProvider());
        public WebApplication App { get; private set; } = null!;
        public HttpClient Client { get; private set; } = null!;
        public HttpClient Anonymous { get; private set; } = null!;
        public string Token { get; set; } = "";
        public IntegrationInstanceDto Instance { get; private set; } = null!;
        public IntegrationConfigurationChange Draft => new(Instance.Revision, Instance.PackageVersion, Instance.PackageDigest,
            Instance.DescriptorDigest, new() { ["region"] = IntegrationJson.Element("eu") }, []);
        public TenantDbContextFactory Factory(string installation) => new(options, installation);
        public async Task<IntegrationOAuthStartDto> StartAsync()
        {
            using var response = await Client.PostAsJsonAsync($"/api/v2/integrations/{Instance.Id}/oauth/start", new IntegrationOAuthStartRequest(Draft, "mobile"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<IntegrationOAuthStartDto>())!;
        }
        public Task<HttpResponseMessage> CallbackAsync() => Anonymous.GetAsync($"/integrations/oauth/callback?state={Executor.Begin!.State}&code=approved");
        public async Task<IntegrationOAuthStatusDto> StatusAsync(Guid id)
        {
            using var response = await Client.GetAsync($"/api/v2/integrations/{Instance.Id}/oauth/{id}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<IntegrationOAuthStatusDto>())!;
        }
        public async Task<string> LoginAsync(string user, bool cookie)
        {
            using var response = await Client.PostAsJsonAsync("/_fixture/login", new Login(user, "LocalOAuth!42", cookie));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        }
        public async Task<string> PrivateStateAsync(string installation)
        {
            await using var db = Factory(installation).CreateDbContext();
            return JsonSerializer.Serialize(new
            {
                instances = await db.IntegrationInstances.AsNoTracking().OrderBy(i => i.Id).ToListAsync(),
                configs = await db.IntegrationConfigurations.AsNoTracking().OrderBy(c => c.InstanceId).ThenBy(c => c.Revision).ToListAsync(),
                devices = await db.IntegrationDeviceBindings.AsNoTracking().ToListAsync(),
                rules = await db.TriggerRules.AsNoTracking().ToListAsync()
            });
        }
        public static async Task<Fixture> CreateAsync()
        {
            IntegrationDescriptorValidator.Validate(await new Catalog().GetAsync("oauth.fixture", null, default));
            var connection = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SOLAR_TEST_SQL_CONNECTION"))
            { InitialCatalog = "IntegrationOAuth_" + Guid.NewGuid().ToString("N") };
            var options = new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlServer(connection.ConnectionString).Options;
            var f = new Fixture(options);
            await using (var db = f.Factory("a").CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                db.Installations.AddRange(new Installation { Id = "a" }, new Installation { Id = "b" });
                db.Users.AddRange(new IdentityUser { Id = "a", UserName = "a" }, new IdentityUser { Id = "b", UserName = "b" });
                db.InstallationMemberships.AddRange(new InstallationMembership { UserId = "a", InstallationId = "a" }, new InstallationMembership { UserId = "b", InstallationId = "b" });
                await db.SaveChangesAsync();
            }
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = AppContext.BaseDirectory });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(options);
            builder.Services.AddScoped(_ => new DeyeSolarDbContext(options));
            builder.Services.AddIdentity<IdentityUser, IdentityRole>().AddEntityFrameworkStores<DeyeSolarDbContext>();
            builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, MobileBearerAuthenticationHandler>(MobileBearerAuthenticationHandler.SchemeName, _ => { });
            builder.Services.AddAuthorization(); builder.Services.AddAntiforgery();
            builder.Services.AddAccountIdentities(new AuthProviderOptions());
            builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            builder.Services.AddSingleton(f.Secrets);
            builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
            builder.Services.AddSingleton<MobileSessionStore>();
            builder.Services.AddSingleton<DeyeSolar.Web.Auth.IAccountSessionStore>(p => p.GetRequiredService<MobileSessionStore>());
            builder.Services.AddSingleton<IntegrationSetupGate>();
            builder.Services.AddSingleton(new IntegrationOAuthOptions { PublicBaseUrl = "https://solar.example" });
            builder.Services.AddSingleton<IOptions<IntegrationRuntimeOptions>>(Options.Create(new IntegrationRuntimeOptions()));
            builder.Services.AddSingleton<IntegrationOAuthService>();
            builder.Services.AddSingleton<IIntegrationProviderCatalog>(new Catalog());
            builder.Services.AddSingleton<IIntegrationSetupExecutor>(f.Executor);
            builder.Services.AddSingleton(new IntegrationChangeNotifier(NullLogger<IntegrationChangeNotifier>.Instance));
            builder.Services.AddScoped<CurrentInstallation>();
            builder.Services.AddScoped<IDbContextFactory<DeyeSolarDbContext>>(p => new RequestFactory(options, p.GetRequiredService<CurrentInstallation>()));
            builder.Services.AddScoped<InstallationMembershipService>();
            builder.Services.AddScoped<DeyeSolar.Web.Auth.IInstallationAccessAuthorizer, DeyeSolar.Web.Auth.InstallationAccessAuthorizer>();
            builder.Services.AddScoped<IIntegrationManagerAccess, IntegrationManagerAccess>();
            builder.Services.AddSingleton<IIntegrationConnectionLifecycle, IntegrationConnectionLifecycle>();
            builder.Services.AddSingleton<IIntegrationConfigurationWriter, IntegrationConfigurationWriter>();
            builder.Services.AddSingleton<IIntegrationConfigurationResolver, IntegrationConfigurationResolver>();
            builder.Services.AddSingleton<IIntegrationSelectionTokens, IntegrationSelectionTokens>();
            builder.Services.AddSingleton<IIntegrationDeviceBindingWriter, IntegrationDeviceBindingWriter>();
            builder.Services.AddScoped<IntegrationSetupService>();
            builder.Services.AddScoped<DynamicSocketGateway>(_ => throw new InvalidOperationException("This OAuth fixture must not execute device commands."));
            builder.Services.AddSingleton<IIntegrationPackageManager>(f.OriginManager);
            builder.Services.AddSingleton<LegacyIntegrationBootstrap>(_ => throw new InvalidOperationException("This OAuth fixture does not bootstrap legacy connections."));
            builder.Services.AddSingleton<TenantRuntimeRegistry>(_ => throw new InvalidOperationException("This OAuth fixture must not start device polling."));
            f.App = builder.Build();
            f.App.UseAuthentication(); f.App.UseAuthorization();
            f.App.Use(async (context, next) =>
            {
                var membership = await context.RequestServices.GetRequiredService<InstallationMembershipService>().ResolveAsync(context.User);
                if (membership is not null) context.RequestServices.GetRequiredService<CurrentInstallation>().BindOnce(membership.InstallationId);
                await next(context);
            });
            f.App.MapDynamicIntegrations();
            f.App.MapPost("/_fixture/login", async (Login login, UserManager<IdentityUser> users, SignInManager<IdentityUser> signIn,
                MobileSessionStore sessions, InstallationMembershipService memberships) =>
            {
                var user = await users.FindByIdAsync(login.User);
                if (user is null || !await users.CheckPasswordAsync(user, login.Password)) return Results.Unauthorized();
                var membership = (await memberships.GetForUserAsync(user.Id))!;
                if (login.Cookie) await signIn.SignInWithClaimsAsync(user, false, [new Claim(InstallationIds.ClaimType, membership.InstallationId)]);
                return Results.Ok(new { token = sessions.Create(user.Id, user.UserName!, user.SecurityStamp, membership.InstallationId).Token });
            });
            using (var scope = f.App.Services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
                foreach (var id in new[] { "a", "b" })
                {
                    var user = (await users.FindByIdAsync(id))!;
                    Assert.True((await users.UpdateAsync(user)).Succeeded);
                    Assert.True((await users.AddPasswordAsync(user, "LocalOAuth!42")).Succeeded);
                }
            }
            await f.App.StartAsync();
            var address = f.App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            f.Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new(address) };
            f.Anonymous = new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new(address) };
            f.Token = await f.LoginAsync("a", false);
            f.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", f.Token);
            using var created = await f.Client.PostAsJsonAsync("/api/v2/integrations", new CreateIntegrationRequest("oauth.fixture", "OAuth A"));
            Assert.True(created.StatusCode == HttpStatusCode.OK, $"{created.StatusCode}: {await created.Content.ReadAsStringAsync()}");
            f.Instance = (await created.Content.ReadFromJsonAsync<IntegrationInstanceDto>())!;
            var tokenB = await f.LoginAsync("b", false);
            f.Client.DefaultRequestHeaders.Authorization = new("Bearer", tokenB);
            using var other = await f.Client.PostAsJsonAsync("/api/v2/integrations", new CreateIntegrationRequest("oauth.fixture", "OAuth B"));
            Assert.True(other.StatusCode == HttpStatusCode.OK, await other.Content.ReadAsStringAsync());
            f.Client.DefaultRequestHeaders.Authorization = new("Bearer", f.Token);
            return f;
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose(); Anonymous.Dispose(); await App.DisposeAsync();
            await using var db = Factory("a").CreateDbContext(); await db.Database.EnsureDeletedAsync();
        }
    }
    private sealed record Login(string User, string Password, bool Cookie);
    private sealed class RequestFactory(DbContextOptions<DeyeSolarDbContext> options, CurrentInstallation current) : IDbContextFactory<DeyeSolarDbContext>
    {
        public DeyeSolarDbContext CreateDbContext() => current.Id is { } id ? new(options, id) : new(options);
        public Task<DeyeSolarDbContext> CreateDbContextAsync(CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(CreateDbContext()); }
    }
    private sealed class Catalog : IIntegrationProviderCatalog
    {
        public Task<IReadOnlyList<IntegrationProviderDescriptor>> GetProvidersAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IntegrationProviderDescriptor>>([Descriptor]);
        public Task<IntegrationProviderDescriptor> GetAsync(string providerId, string? version, CancellationToken ct) => Task.FromResult(Descriptor);
        private static IntegrationProviderDescriptor Descriptor => new("oauth.fixture", "1.0.0", "package-sha", "descriptor-sha", "OAuth fixture", 1, 1,
            ["text", "secret", "oauth"], [new("region", "text", "Region", true), new("accessToken", "secret", "Access token", true, Secret: true)],
            ["test", "discover", "oauth"], OAuthDefinition: new(["accessToken"]));
    }
    private sealed class Executor : IIntegrationSetupExecutor
    {
        public IntegrationOAuthBeginRequest? Begin { get; private set; }
        public int CompleteCalls;
        public TaskCompletionSource? Started { get; set; }
        public TaskCompletionSource? Release { get; set; }
        public string AccountIdentity { get; set; } = "account-a";
        public Task<IntegrationTestResult> TestAsync(ProviderPackageIdentity package, IntegrationDraftConfiguration draft, CancellationToken ct)
            => Task.FromResult(new IntegrationTestResult(draft.Secrets.GetValueOrDefault("accessToken") is "provider-access-token" or "manual-new-token", "ok", "Verified.", "account-a"));
        public Task<IReadOnlyList<IntegrationDiscoveredDevice>> DiscoverAsync(ProviderPackageIdentity package, IntegrationDraftConfiguration draft, IntegrationDiscoveryQuery query, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<IntegrationDiscoveredDevice>>([]);
        public Task<IntegrationOAuthBeginResult> BeginAuthorizationAsync(ProviderPackageIdentity package, IntegrationDraftConfiguration draft, IntegrationOAuthBeginRequest request, CancellationToken ct)
        { Begin = request; return Task.FromResult(new IntegrationOAuthBeginResult("https://provider.example/authorize?state=" + request.State)); }
        public async Task<IntegrationOAuthCompleteResult> CompleteAuthorizationAsync(ProviderPackageIdentity package, IntegrationDraftConfiguration draft, IntegrationOAuthCompleteRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref CompleteCalls); Started?.TrySetResult();
            if (Release is not null) await Release.Task.WaitAsync(ct);
            Assert.Equal("approved", request.Code); Assert.Equal(Begin!.RedirectUri, request.RedirectUri); Assert.Equal(43, request.CodeVerifier.Length);
            return new(true, IntegrationJson.Element(new Dictionary<string, string>()), new Dictionary<string, string> { ["accessToken"] = "provider-access-token" }, AccountIdentity);
        }
    }
    private sealed class OriginManager : IIntegrationPackageManager
    {
        public List<string> Approved { get; } = [];
        public Task ApproveOriginAsync(string origin, CancellationToken ct) { Approved.Add(origin); return Task.CompletedTask; }
        public Task<IntegrationInstalledPackage> InstallAsync(IntegrationPackageInstallRequest request, CancellationToken ct) => throw new InvalidOperationException("This OAuth fixture must not install packages.");
        public Task<IntegrationInstalledPackage> ResolveAsync(ProviderPackageIdentity identity, CancellationToken ct) => throw new NotSupportedException();
    }
}
