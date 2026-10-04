using System.Security.Claims;
using DeyeSolar.Web.Api;
using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DeyeSolar.Web.Tests;

public class AccountIdentityTests
{
    private sealed class SqliteModelContext(DbContextOptions<DeyeSolarDbContext> options) : DeyeSolarDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            // SQLite cannot order DateTimeOffset directly; this test-only conversion keeps production SQL Server unchanged.
            modelBuilder.Entity<Installation>().Property(i => i.CreatedAt).HasConversion<long>();
        }
    }
    private sealed class Factory(DbContextOptions<DeyeSolarDbContext> options) : IDbContextFactory<DeyeSolarDbContext>
    {
        public DeyeSolarDbContext CreateDbContext() => new(options);
        public Task<DeyeSolarDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public readonly SqliteConnection Connection = new("Data Source=:memory:");
        public ServiceProvider Provider = null!;
        public DbContextOptions<DeyeSolarDbContext> Options = null!;
        public async Task InitializeAsync()
        {
            await Connection.OpenAsync(); Connection.CreateCollation("Latin1_General_100_BIN2", string.CompareOrdinal);
            Options = new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlite(Connection).Options;
            await using var db = new SqliteModelContext(Options); await db.Database.EnsureCreatedAsync();
            Options = new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlite(Connection).UseModel(db.Model).Options;
            db.Installations.Add(new Installation { Id = InstallationIds.Legacy, CreatedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync();
            var services = new ServiceCollection(); services.AddLogging(); services.AddScoped(_ => new DeyeSolarDbContext(Options));
            services.AddSingleton<IDbContextFactory<DeyeSolarDbContext>>(new Factory(Options));
            services.AddIdentity<IdentityUser, IdentityRole>(options =>
            {
                options.Password.RequiredLength = 12; options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireDigit = false; options.Password.RequireUppercase = false; options.Password.RequireLowercase = false;
            }).AddEntityFrameworkStores<DeyeSolarDbContext>();
            services.AddScoped<InstallationMembershipService>(); services.AddScoped<AccountIdentityService>(); services.AddSingleton<MobileSessionStore>();
            services.AddSingleton<DeyeSolar.Web.Auth.IAccountSessionStore>(p => p.GetRequiredService<MobileSessionStore>());
            services.AddSingleton(new AuthProviderOptions());
            Provider = services.BuildServiceProvider();
        }
        public async ValueTask DisposeAsync() { await Provider.DisposeAsync(); await Connection.DisposeAsync(); }
    }

    [Fact]
    public async Task VerifiedRegistrationGetsEmptyInstallationAndCannotSeeLegacySecrets()
    {
        await using var fixture = new Fixture(); await fixture.InitializeAsync();
        await using (var legacy = new DeyeSolarDbContext(fixture.Options, InstallationIds.Legacy))
        { legacy.AppSettings.Add(new AppSetting { Section = "DeyeCloud", Key = "Password", Value = "legacy-private-value" }); await legacy.SaveChangesAsync(); }
        using var scope = fixture.Provider.CreateScope(); var accounts = scope.ServiceProvider.GetRequiredService<AccountIdentityService>();
        var user = await accounts.RegisterAsync(new("email", "owner@example.test"), "long-local-test-password", default);
        var session = await accounts.SessionAsync(user.Id, default); Assert.NotNull(session); Assert.Equal("owner@example.test", session.Username);
        Assert.NotEqual(InstallationIds.Legacy, session.InstallationId); Assert.True(user.EmailConfirmed);
        await using var own = new DeyeSolarDbContext(fixture.Options, session.InstallationId!);
        Assert.Empty(await own.AppSettings.ToListAsync()); Assert.Empty(await own.Readings.ToListAsync()); Assert.Empty(await own.TriggerRules.ToListAsync());
        Assert.Equal(2, await own.Installations.CountAsync());
        var memberships = await own.InstallationMemberships.ToListAsync(); Assert.Equal(session.InstallationId, Assert.Single(memberships).InstallationId);
        await Assert.ThrowsAsync<AccountIdentityException>(() => accounts.RegisterAsync(new("email", "OWNER@example.test"), "long-local-test-password", default));
    }

    [Fact]
    public async Task EmailPhoneAndGoogleRequireExplicitProofToLinkOneExistingAccount()
    {
        await using var fixture = new Fixture(); await fixture.InitializeAsync(); using var scope = fixture.Provider.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<AccountIdentityService>();
        var user = await accounts.RegisterAsync(new("email", "owner@example.test"), "long-local-test-password", default);
        var collision = await Assert.ThrowsAsync<AccountIdentityException>(() => accounts.GoogleAsync("google-subject", "owner@example.test", true, null, default));
        Assert.Equal("link_required", collision.Code);
        var linked = await accounts.GoogleAsync("google-subject", "owner@example.test", true, user.Id, default); Assert.Equal(user.Id, linked.Id);
        await accounts.LinkAsync(user.Id, new("phone", "+48123456789"), default);
        Assert.Equal(user.Id, (await accounts.FindVerifiedAsync(new("phone", "+48123456789"), default))!.Id);
        var known = await accounts.GoogleAsync("google-subject", "owner@example.test", true, null, default); Assert.Equal(user.Id, known.Id);
        var second = await accounts.RegisterAsync(new("email", "another@example.test"), "long-local-test-password", default);
        var conflict = await Assert.ThrowsAsync<AccountIdentityException>(() => accounts.LinkAsync(second.Id, new("phone", "+48123456789"), default));
        Assert.Equal("link_conflict", conflict.Code);
        Assert.Equal("link_conflict", (await Assert.ThrowsAsync<AccountIdentityException>(() => accounts.GoogleAsync("google-subject", "owner@example.test", true, second.Id, default))).Code);
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>(); Assert.Equal(2, await users.Users.CountAsync());
    }

    [Fact]
    public async Task FailedGoogleProviderBindingRollsBackAccountAndInstallationTogether()
    {
        await using var fixture = new Fixture(); await fixture.InitializeAsync();
        await using (var db = new DeyeSolarDbContext(fixture.Options))
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_google BEFORE INSERT ON AspNetUserLogins BEGIN SELECT RAISE(FAIL, 'Synthetic provider binding failure'); END;");
        using var scope = fixture.Provider.CreateScope(); var accounts = scope.ServiceProvider.GetRequiredService<AccountIdentityService>();
        await Assert.ThrowsAsync<DbUpdateException>(() => accounts.GoogleAsync("google-new-subject", "new@example.test", true, null, default));
        await using var check = new DeyeSolarDbContext(fixture.Options);
        Assert.Empty(await check.Users.ToListAsync()); Assert.Empty(await check.InstallationMemberships.ToListAsync());
        Assert.Equal(InstallationIds.Legacy, Assert.Single(await check.Installations.ToListAsync()).Id);
    }

    [Fact]
    public async Task UnverifiedGoogleAndPasswordsTooShortDoNotCreateAccount()
    {
        await using var fixture = new Fixture(); await fixture.InitializeAsync(); using var scope = fixture.Provider.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<AccountIdentityService>();
        await Assert.ThrowsAsync<AccountIdentityException>(() => accounts.GoogleAsync("google-new-subject", "new@example.test", false, null, default));
        await Assert.ThrowsAsync<AccountIdentityException>(() => accounts.RegisterAsync(new("email", "new@example.test"), "short", default));
        await using var check = new DeyeSolarDbContext(fixture.Options); Assert.Empty(await check.Users.ToListAsync()); Assert.Single(await check.Installations.ToListAsync());
    }

    [Fact]
    public async Task MembershipResolutionRejectsForgedSelectionAndDeletedMembership()
    {
        await using var fixture = new Fixture(); await fixture.InitializeAsync(); using var scope = fixture.Provider.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<AccountIdentityService>();
        var user = await accounts.RegisterAsync(new("phone", "+48123456789"), "long-local-test-password", default);
        var resolver = scope.ServiceProvider.GetRequiredService<InstallationMembershipService>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id)], "test"));
        var own = await resolver.ResolveAsync(principal); Assert.NotNull(own); Assert.NotEqual(InstallationIds.Legacy, own.InstallationId);
        var forged = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id), new Claim(InstallationIds.ClaimType, InstallationIds.Legacy)], "test"));
        Assert.Null(await resolver.ResolveAsync(forged));
        await using var db = new DeyeSolarDbContext(fixture.Options); db.InstallationMemberships.Remove(await db.InstallationMemberships.SingleAsync()); await db.SaveChangesAsync();
        Assert.Null((await accounts.SessionAsync(user.Id, default))!.InstallationId);
    }
}
