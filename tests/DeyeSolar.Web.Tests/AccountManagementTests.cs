using System.IO.Compression;
using System.Security.Claims;
using System.Text.Json;
using DeyeSolar.Web.Api;
using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Redesign;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DeyeSolar.Web.Tests;

public sealed partial class AccountManagementTests
{
    private const string Password = "Original test password 42!";
    private sealed class SqliteModel(DbContextOptions<DeyeSolarDbContext> options) : DeyeSolarDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            base.OnModelCreating(model);
            model.Entity<Installation>().Property(i => i.CreatedAt).HasConversion<long>();
        }
    }
    private sealed class Delivery : IIdentityVerificationDelivery
    {
        public Dictionary<string, string> Codes { get; } = new();
        public Task SendEmailAsync(string destination, string code, CancellationToken ct) { Codes[destination] = code; return Task.CompletedTask; }
        public Task SendPhoneAsync(string destination, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> CheckPhoneAsync(string destination, string code, CancellationToken ct) => Task.FromResult(false);
    }
    private sealed class Factory(DbContextOptions<DeyeSolarDbContext> options) : IDbContextFactory<DeyeSolarDbContext>
    {
        public DeyeSolarDbContext CreateDbContext() => new(options);
        public Task<DeyeSolarDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public SqliteConnection Connection = new("Data Source=:memory:");
        public DbContextOptions<DeyeSolarDbContext> Options = null!;
        public ServiceProvider Services = null!;
        public Delivery Delivery = new();
        public async Task InitAsync()
        {
            await Connection.OpenAsync(); Connection.CreateCollation("Latin1_General_100_BIN2", string.CompareOrdinal);
            Options = new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlite(Connection).Options;
            await using var db = new SqliteModel(Options); await db.Database.EnsureCreatedAsync();
            Options = new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlite(Connection).UseModel(db.Model).Options;
            var services = new ServiceCollection(); services.AddLogging(); services.AddHttpContextAccessor();
            services.AddScoped(_ => new DeyeSolarDbContext(Options)); services.AddSingleton(Options);
            services.AddSingleton<IDbContextFactory<DeyeSolarDbContext>>(new Factory(Options));
            services.AddIdentity<IdentityUser, IdentityRole>().AddEntityFrameworkStores<DeyeSolarDbContext>();
            services.AddSingleton<TimeProvider>(TimeProvider.System); services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            services.AddSingleton(new AuthProviderOptions { ResendApiKey = "synthetic-key", EmailFrom = "test@example.test", GoogleClientId = "synthetic-id", GoogleClientSecret = "synthetic-secret" });
            services.AddSingleton<IIdentityVerificationDelivery>(Delivery); services.AddSingleton<OneTimeVerificationService>();
            services.AddSingleton<IAccountSessionStore, MobileSessionStore>(); services.AddSingleton<ExternalAccountProofStore>(); services.AddSingleton<ContactChangeStore>();
            services.AddSingleton<AppleIdentityCredentialStore>(); services.AddScoped<InstallationMembershipService>(); services.AddScoped<AccountIdentityService>();
            services.AddScoped<AccountFreshProofVerifier>(); services.AddScoped<AccountZipExporter>(); services.AddScoped<AccountManagementService>();
            Services = services.BuildServiceProvider();
        }
        public async ValueTask DisposeAsync() { await Services.DisposeAsync(); await Connection.DisposeAsync(); }
    }
    private static ClaimsPrincipal Actor(IdentityUser user, MobileSession session) => new(new ClaimsIdentity(new[] {
        new Claim(ClaimTypes.NameIdentifier, user.Id), new Claim(InstallationAccessAuthorizer.StampClaim, user.SecurityStamp!), new Claim(InstallationAccessAuthorizer.SessionClaim, session.Token) }, "fixture"));
    private static async Task<ClaimsPrincipal> ActorAsync(IServiceProvider services, IdentityUser user)
        => Actor(user, await services.GetRequiredService<IAccountSessionStore>().CreateAsync(user.Id, user.UserName!, user.SecurityStamp, null));

    [Fact]
    public async Task ProfileAndPersonalZoneChangeOnlyAccountClaimsAndRejectInvalidInput()
    {
        await using var fixture = new Fixture(); await fixture.InitAsync(); using var scope = fixture.Services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<AccountIdentityService>(); var user = await accounts.RegisterAsync(new("email", "owner@example.test"), Password, default);
        var actor = await ActorAsync(scope.ServiceProvider, user); var management = scope.ServiceProvider.GetRequiredService<AccountManagementService>();
        Assert.Equal("Smart Owner", (await management.UpdateProfileAsync(actor, "  Smart Owner  ", default)).DisplayName);
        Assert.Equal("Europe/Warsaw", (await management.UpdatePreferencesAsync(actor, "Europe/Warsaw", default)).DisplayTimeZoneId);
        Assert.Equal("owner@example.test", (await management.ProfileAsync(user.Id, default)).VerifiedEmail);
        await Assert.ThrowsAsync<AccountSecurityException>(() => management.UpdateProfileAsync(actor, "bad\nname", default));
        await Assert.ThrowsAsync<AccountSecurityException>(() => management.UpdatePreferencesAsync(actor, "Invented/Zone", default));
        await using var db = new DeyeSolarDbContext(fixture.Options);
        Assert.Empty(await db.AppSettings.IgnoreQueryFilters().ToListAsync()); Assert.Single(await db.Installations.ToListAsync());
        Assert.Equal(user.SecurityStamp, (await db.Users.SingleAsync()).SecurityStamp);
    }
    [Fact]
    public async Task VerifiedContactReplacementPreservesCurrentAddressUntilSingleUseCompletion()
    {
        await using var fixture = new Fixture(); await fixture.InitAsync(); using var scope = fixture.Services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<AccountIdentityService>(); var user = await accounts.RegisterAsync(new("email", "old@example.test"), Password, default);
        var actor = await ActorAsync(scope.ServiceProvider, user); var service = scope.ServiceProvider.GetRequiredService<AccountManagementService>();
        var challenge = await service.StartContactChangeAsync(actor, "email", "NEW@example.test", new(Password), default);
        Assert.Equal("old@example.test", (await service.ProfileAsync(user.Id, default)).VerifiedEmail);
        await Assert.ThrowsAsync<AccountSecurityException>(() => service.CompleteContactChangeAsync(actor, challenge.ChallengeId, "invalid", default));
        var profile = await service.CompleteContactChangeAsync(actor, challenge.ChallengeId, fixture.Delivery.Codes["new@example.test"], default);
        Assert.Equal("new@example.test", profile.VerifiedEmail);
        await Assert.ThrowsAsync<AccountSecurityException>(() => service.CompleteContactChangeAsync(actor, challenge.ChallengeId, fixture.Delivery.Codes["new@example.test"], default));
        await using var db = new DeyeSolarDbContext(fixture.Options);
        Assert.Single(await db.BillingAccounts.ToListAsync()); Assert.Single(await db.InstallationMemberships.ToListAsync());
    }
    [Fact]
    public async Task ContactChangeIsBoundToStartingSessionAndRechecksDestinationOwnership()
    {
        await using var fixture = new Fixture(); await fixture.InitAsync(); using var scope = fixture.Services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<AccountIdentityService>(); var user = await accounts.RegisterAsync(new("email", "old@example.test"), Password, default);
        var actor = await ActorAsync(scope.ServiceProvider, user); var secondActor = await ActorAsync(scope.ServiceProvider, user);
        var service = scope.ServiceProvider.GetRequiredService<AccountManagementService>();
        var challenge = await service.StartContactChangeAsync(actor, "email", "new@example.test", new(Password), default);
        var denied = await Assert.ThrowsAsync<AccountSecurityException>(() => service.CompleteContactChangeAsync(secondActor, challenge.ChallengeId, fixture.Delivery.Codes["new@example.test"], default));
        Assert.Equal("contact_change_expired", denied.Code); Assert.Equal("old@example.test", (await service.ProfileAsync(user.Id, default)).VerifiedEmail);
        var collision = await service.StartContactChangeAsync(actor, "email", "claimed@example.test", new(Password), default);
        _ = await accounts.RegisterAsync(new("email", "claimed@example.test"), Password, default);
        Assert.Equal("contact_conflict", (await Assert.ThrowsAsync<AccountSecurityException>(() => service.CompleteContactChangeAsync(actor, collision.ChallengeId, fixture.Delivery.Codes["claimed@example.test"], default))).Code);
        Assert.Equal("old@example.test", (await service.ProfileAsync(user.Id, default)).VerifiedEmail);
    }
    [Fact]
    public async Task AppleIdentityDoesNotMergeMatchingEmailAndUnlinkPersistsEncryptedRevocation()
    {
        await using var fixture = new Fixture(); await fixture.InitAsync(); using var scope = fixture.Services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<AccountIdentityService>(); var credentials = scope.ServiceProvider.GetRequiredService<AppleIdentityCredentialStore>();
        var user = await accounts.RegisterAsync(new("email", "owner@example.test"), Password, default);
        Assert.Equal("link_required", (await Assert.ThrowsAsync<AccountIdentityException>(() => accounts.AppleAsync(new("apple-subject", "owner@example.test", true), "com.dshapar.solar", "synthetic-refresh", null, credentials, default))).Code);
        await accounts.AppleAsync(new("apple-subject", "owner@example.test", true), "com.dshapar.solar", "synthetic-refresh", user.Id, credentials, default);
        Assert.True((await accounts.IdentitiesAsync(user.Id, default))!.AppleLinked);
        var actor = await ActorAsync(scope.ServiceProvider, user); var service = scope.ServiceProvider.GetRequiredService<AccountManagementService>();
        Assert.False((await service.UnlinkAsync(actor, "Apple", new(Password), default))!.AppleLinked);
        await using var db = new DeyeSolarDbContext(fixture.Options);
        Assert.Empty(await db.AppleIdentityCredentials.ToListAsync());
        var pending = Assert.Single(await db.AppleIdentityRevocations.ToListAsync());
        Assert.DoesNotContain("synthetic-refresh", pending.ProtectedRefreshToken); Assert.Equal("synthetic-refresh", credentials.Unprotect(pending.ProtectedRefreshToken));
        Assert.Single(await db.BillingAccounts.ToListAsync()); Assert.Single(await db.Installations.ToListAsync());
    }
    [Fact]
    public async Task LastUsableExternalSignInMethodCannotBeUnlinked()
    {
        await using var fixture = new Fixture(); await fixture.InitAsync(); using var scope = fixture.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>(); var user = new IdentityUser { UserName = "external-only" };
        Assert.True((await users.CreateAsync(user)).Succeeded); Assert.True((await users.AddLoginAsync(user, new("Google", "only-subject", "Google"))).Succeeded);
        var actor = await ActorAsync(scope.ServiceProvider, user); var proofs = scope.ServiceProvider.GetRequiredService<ExternalAccountProofStore>();
        var id = proofs.Complete(proofs.Start(actor, user, "Google", "identity-unlink").Id, "Google", user.Id);
        var service = scope.ServiceProvider.GetRequiredService<AccountManagementService>();
        Assert.Equal("last_sign_in_method", (await Assert.ThrowsAsync<AccountSecurityException>(() => service.UnlinkAsync(actor, "Google", new(ExternalProofId: id), default))).Code);
        Assert.Single(await users.GetLoginsAsync(user));
    }
    [Fact]
    public async Task ZipIncludesOwnedSettingsAndActivityButOmitsSiblingHistoryAndAllCredentials()
    {
        await using var fixture = new Fixture(); await fixture.InitAsync(); using var scope = fixture.Services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<AccountIdentityService>(); var user = await accounts.RegisterAsync(new("email", "owner@example.test"), Password, default);
        var other = await accounts.RegisterAsync(new("email", "other@example.test"), Password, default);
        await using var db = new DeyeSolarDbContext(fixture.Options);
        var ownId = await db.InstallationMemberships.Where(m => m.UserId == user.Id).Select(m => m.InstallationId).SingleAsync();
        var otherId = await db.InstallationMemberships.Where(m => m.UserId == other.Id).Select(m => m.InstallationId).SingleAsync();
        await using (var own = new DeyeSolarDbContext(fixture.Options, ownId))
        {
            own.AppSettings.Add(new() { Section = "Display", Key = "TimeZoneId", Value = "Europe/Warsaw" });
            own.AppSettings.Add(new() { Section = "DeyeCloud", Key = "Password", Value = "SECRET-EXCLUDED" });
            own.ActivityEvents.Add(new() { Kind = "rule-created", RuleName = "owned-activity", OccurredAt = DateTime.UtcNow, RecordedAt = DateTime.UtcNow });
            own.Readings.Add(new() { Timestamp = DateTime.UtcNow, SolarProduction = 123 }); await own.SaveChangesAsync();
        }
        await using (var sibling = new DeyeSolarDbContext(fixture.Options, otherId))
        { sibling.ActivityEvents.Add(new() { Kind = "rule-created", RuleName = "SIBLING-EXCLUDED", OccurredAt = DateTime.UtcNow, RecordedAt = DateTime.UtcNow }); await sibling.SaveChangesAsync(); }
        var actor = await ActorAsync(scope.ServiceProvider, user);
        var zip = await scope.ServiceProvider.GetRequiredService<AccountManagementService>().ExportZipAsync(actor, new(Password), default);
        using var archive = new ZipArchive(new MemoryStream(zip)); Assert.NotNull(archive.GetEntry("manifest.json"));
        var text = new List<string>(); foreach (var entry in archive.Entries) { using var reader = new StreamReader(entry.Open()); text.Add(await reader.ReadToEndAsync()); }
        var all = string.Join("\n", text);
        Assert.Contains("owned-activity", all); Assert.Contains("Europe/Warsaw", all); Assert.DoesNotContain("SECRET-EXCLUDED", all); Assert.DoesNotContain("SIBLING-EXCLUDED", all);
        Assert.DoesNotContain("passwordHash", all); Assert.DoesNotContain("tokenHash", all); Assert.DoesNotContain("protectedRefreshToken", all);
        using var account = JsonDocument.Parse(text[archive.Entries.ToList().FindIndex(e => e.FullName == "account.json")]);
        Assert.Equal(user.Id, account.RootElement.GetProperty("id").GetString());
    }
}
