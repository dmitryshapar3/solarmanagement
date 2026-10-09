using System.Security.Claims;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Api;
using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Tenancy;
using DeyeSolar.Web.Integrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
namespace DeyeSolar.Web.Tests;
public class PersistentSessionSecurityTests
{
    [SqlServerFact]
    public async Task SessionsSurviveNewStoreAndAreHashedBoundedAndRevocable()
    {
        await using var fixture = await Fixture.StartAsync();
        var store = MobileSessionStore.Persistent(fixture.Options, fixture.Clock);
        var issued = new List<MobileSession>();
        for (var i = 0; i < MobileSessionStore.MaximumSessionsPerUser + 2; i++)
        {
            fixture.Clock.Now = fixture.Clock.Now.AddSeconds(1);
            issued.Add(await store.CreateAsync("owner", "owner", "stamp", "installation"));
        }
        var restarted = MobileSessionStore.Persistent(fixture.Options, fixture.Clock);
        Assert.Null(await restarted.FindAsync(issued[0].Token));
        Assert.NotNull(await restarted.FindAsync(issued[^1].Token));
        await using (var db = new DeyeSolarDbContext(fixture.Options))
        {
            var saved = await db.AccountSessions.ToListAsync();
            Assert.Equal(MobileSessionStore.MaximumSessionsPerUser, saved.Count);
            Assert.All(saved, s => Assert.DoesNotContain(issued, original => original.Token == s.TokenHash));
            Assert.DoesNotContain("Token", db.Model.FindEntityType(typeof(AccountSessionEntity))!.GetProperties().Select(p => p.Name));
        }
        await restarted.RevokeAsync(issued[^1].Token);
        Assert.Null(await store.FindAsync(issued[^1].Token));
        fixture.Clock.Now = fixture.Clock.Now.AddDays(31);
        Assert.Null(await store.FindAsync(issued[^2].Token));
        await store.RevokeUserAsync("owner");
        await using var final = new DeyeSolarDbContext(fixture.Options);
        Assert.Empty(await final.AccountSessions.ToListAsync());
    }
    [SqlServerFact]
    public async Task PublicSessionIdsAreStablePrivateAndRevocationIsAccountScopedAndPreservesCurrent()
    {
        await using var fixture = await Fixture.StartAsync();
        var store = MobileSessionStore.Persistent(fixture.Options, fixture.Clock);
        var current = await store.CreateAsync("owner", "owner", "stamp", "installation");
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(1);
        var other = await store.CreateAsync("owner", "owner", "stamp", "installation");
        var rows = await store.ListAsync("owner", current.Token);
        Assert.Equal(2, rows.Count); Assert.Equal(current.SessionId, Assert.Single(rows.Where(x => x.IsCurrent)).Id);
        Assert.DoesNotContain(current.Token, System.Text.Json.JsonSerializer.Serialize(rows));
        Assert.False(await store.RevokeSessionAsync("different-account", other.SessionId));
        Assert.NotNull(await store.FindAsync(other.Token));
        await store.RevokeOthersAsync("owner", current.Token);
        Assert.Null(await store.FindAsync(other.Token));
        var reloaded = await MobileSessionStore.Persistent(fixture.Options, fixture.Clock).FindAsync(current.Token);
        Assert.Equal(current.SessionId, reloaded!.SessionId);
        Assert.True(await store.RevokeSessionAsync("owner", current.SessionId)); Assert.Null(await store.FindAsync(current.Token));
    }
    [SqlServerFact]
    public async Task SessionActivityUpdatesAreThrottledAndLegacyMetadataStaysUnknown()
    {
        await using var fixture = await Fixture.StartAsync(); var store = MobileSessionStore.Persistent(fixture.Options, fixture.Clock);
        var session = await store.CreateAsync("owner", "owner", "stamp", "installation");
        await store.FindAsync(session.Token);
        var first = Assert.Single(await store.ListAsync("owner", session.Token)); Assert.NotNull(first.LastSeenAt); Assert.Null(first.Platform); Assert.Null(first.Client);
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(4); await store.FindAsync(session.Token);
        Assert.Equal(first.LastSeenAt, Assert.Single(await store.ListAsync("owner", session.Token)).LastSeenAt);
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(2); await store.FindAsync(session.Token);
        Assert.True(Assert.Single(await store.ListAsync("owner", session.Token)).LastSeenAt > first.LastSeenAt);
    }
    [SqlServerFact]
    public async Task LiveCircuitOperationRechecksStampMembershipAndRoleBeforeCallingRepository()
    {
        await using var fixture = await Fixture.StartAsync();
        var store = MobileSessionStore.Persistent(fixture.Options, fixture.Clock);
        var token = await store.CreateAsync("owner", "owner", "stamp", "installation");
        var actor = new ClaimsPrincipal(new ClaimsIdentity(new[] {
            new Claim(ClaimTypes.NameIdentifier, "owner"), new Claim(InstallationAccessAuthorizer.StampClaim, "stamp"),
            new Claim(InstallationAccessAuthorizer.SessionClaim, token.Token) }, IdentityConstants.ApplicationScheme));
        var current = new CurrentInstallation(); current.BindOnce("installation");
        var security = new InteractiveSecurityContext(new InstallationAccessAuthorizer(fixture.Options, store, fixture.Clock), current, new HttpContextAccessor());
        security.BindOnce(actor);
        var inner = new MutationProbe();
        var repository = new AuthorizedRuleRepository(inner, security);
        await repository.CreateAsync(new TriggerRule(), default);
        Assert.Equal(1, inner.Mutations);
        await using (var db = new DeyeSolarDbContext(fixture.Options))
            await db.InstallationMemberships.Where(m => m.UserId == "owner").ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, "Viewer"));
        Assert.Equal(403, (await Assert.ThrowsAsync<InstallationAccessException>(() => security.EnsureAsync(InstallationPermission.ControlDevices))).Status);
        await security.EnsureAsync(InstallationPermission.Read);
        Assert.Equal(403, (await Assert.ThrowsAsync<InstallationAccessException>(() => repository.UpdateAsync(new TriggerRule(), default))).Status);
        Assert.Equal(1, inner.Mutations);
        await using (var db = new DeyeSolarDbContext(fixture.Options))
            await db.Users.Where(u => u.Id == "owner").ExecuteUpdateAsync(s => s.SetProperty(u => u.SecurityStamp, "revoked"));
        Assert.Equal(401, (await Assert.ThrowsAsync<InstallationAccessException>(() => security.EnsureAsync(InstallationPermission.Read))).Status);
        await using (var db = new DeyeSolarDbContext(fixture.Options))
        {
            await db.Users.Where(u => u.Id == "owner").ExecuteUpdateAsync(s => s.SetProperty(u => u.SecurityStamp, "stamp"));
            await db.InstallationMemberships.Where(m => m.UserId == "owner").ExecuteDeleteAsync();
        }
        Assert.Equal(403, (await Assert.ThrowsAsync<InstallationAccessException>(() => security.EnsureAsync(InstallationPermission.Read))).Status);
    }
    [SqlServerFact]
    public async Task ConcurrentSessionIssuanceCannotExceedUserBoundAndStaleStampCannotIssue()
    {
        await using var fixture = await Fixture.StartAsync();
        var store = MobileSessionStore.Persistent(fixture.Options, fixture.Clock);
        await Task.WhenAll(Enumerable.Range(0, 15).Select(_ => store.CreateAsync("owner", "owner", "stamp", "installation")));
        await using var db = new DeyeSolarDbContext(fixture.Options);
        Assert.Equal(MobileSessionStore.MaximumSessionsPerUser, await db.AccountSessions.CountAsync());
        await db.Users.Where(u => u.Id == "owner").ExecuteUpdateAsync(s => s.SetProperty(u => u.SecurityStamp, "changed"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.CreateAsync("owner", "owner", "stamp", "installation"));
    }
    [SqlServerFact]
    public async Task PasswordChangesRequireFreshProofAndRevokeEveryPersistedSession()
    {
        await using var fixture = await Fixture.StartAsync();
        var store = MobileSessionStore.Persistent(fixture.Options, fixture.Clock);
        using var provider = fixture.Services(store);
        using var scope = provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = (await users.FindByIdAsync("owner"))!;
        Assert.True((await users.AddPasswordAsync(user, "Original password 42!")).Succeeded);
        var session = await store.CreateAsync(user.Id, user.UserName!, user.SecurityStamp, "installation");
        var second = await store.CreateAsync(user.Id, user.UserName!, user.SecurityStamp, "installation");
        var actor = Actor(user, session);
        var service = scope.ServiceProvider.GetRequiredService<AccountSecurityService>();
        var refused = await Assert.ThrowsAsync<AccountSecurityException>(() => service.ChangePasswordAsync(actor,
            new(new("wrong password"), "New secure password 43!"), default));
        Assert.Equal("fresh_proof_required", refused.Code);
        Assert.NotNull(await store.FindAsync(session.Token));
        await service.ChangePasswordAsync(actor, new(new("Original password 42!"), "New secure password 43!"), default);
        Assert.Null(await store.FindAsync(session.Token)); Assert.Null(await store.FindAsync(second.Token));
        user = (await users.FindByIdAsync("owner"))!;
        Assert.True(await users.CheckPasswordAsync(user, "New secure password 43!"));
        await Assert.ThrowsAsync<AccountSecurityException>(() => service.ExportAsync(actor, new("New secure password 43!"), default));
    }
    [SqlServerFact]
    public async Task SharedAccountDeletionConflictsWithoutRemovingOtherMembersOrDisablingInstallation()
    {
        await using var fixture = await Fixture.StartAsync();
        await using (var db = new DeyeSolarDbContext(fixture.Options))
        {
            db.Users.Add(new IdentityUser { Id = "other", UserName = "other", NormalizedUserName = "OTHER", SecurityStamp = "other-stamp" });
            db.InstallationMemberships.Add(new() { UserId = "other", InstallationId = "installation", Role = "Viewer" });
            await db.SaveChangesAsync();
        }
        await using var registry = Registry();
        var deletion = new AccountDeletionService(fixture.Options, registry);
        var refused = await Assert.ThrowsAsync<AccountSecurityException>(() => deletion.DeleteAsync(new IdentityUser { Id = "owner" }, default));
        Assert.Equal("ownership_transfer_required", refused.Code);
        await using var check = new DeyeSolarDbContext(fixture.Options);
        Assert.Equal(2, await check.Users.CountAsync()); Assert.Equal(2, await check.InstallationMemberships.CountAsync());
        Assert.True((await check.Installations.SingleAsync(i => i.Id == "installation")).IsEnabled);
    }
    [SqlServerFact]
    public async Task UnresolvedCommandBlocksDeletionAndTerminalHistoryIsDeletedWithOwnInstallationOnly()
    {
        await using var fixture = await Fixture.StartAsync();
        await using (var db = new DeyeSolarDbContext(fixture.Options, "installation"))
        {
            db.Installations.Add(new Installation { Id = "neighbour", CreatedAt = DateTimeOffset.UtcNow });
            db.Users.Add(new IdentityUser { Id = "other", UserName = "other", NormalizedUserName = "OTHER", SecurityStamp = "other-stamp" });
            db.InstallationMemberships.Add(new() { UserId = "owner", InstallationId = "neighbour", Role = "Viewer" });
            db.InstallationMemberships.Add(new() { UserId = "other", InstallationId = "neighbour", Role = "Owner" });
            db.ExportFeedPrices.Add(new() { SourceKey = new string('a', 64), StartUtc = DateTime.UtcNow, RetrievedAtUtc = DateTime.UtcNow, PricePlnPerMwh = 345.678m });
            var instance = Guid.NewGuid(); var device = Guid.NewGuid();
            db.IntegrationInstances.Add(new() { Id = instance, InstallationId = "installation", ProviderId = "fixture", Name = "Fixture", PackageVersion = "1", PackageDigest = "digest", DescriptorDigest = "descriptor" });
            db.IntegrationDeviceBindings.Add(new() { Id = device, InstallationId = "installation", InstanceId = instance, RemoteId = "fixture-device", Kind = "smart-socket" });
            db.IntegrationCommands.Add(new() { Id = Guid.NewGuid(), InstallationId = "installation", InstanceId = instance, DeviceId = device, Status = "uncertain", PayloadHash = "hash" });
            await db.SaveChangesAsync();
        }
        await using var registry = Registry();
        var deletion = new AccountDeletionService(fixture.Options, registry);
        var user = new IdentityUser { Id = "owner", SecurityStamp = "stamp" };
        Assert.Equal("unresolved_commands", (await Assert.ThrowsAsync<AccountSecurityException>(() => deletion.DeleteAsync(user, default))).Code);
        await using (var db = new DeyeSolarDbContext(fixture.Options))
        {
            Assert.True((await db.Installations.SingleAsync(i => i.Id == "installation")).IsEnabled);
            await db.IntegrationCommands.IgnoreQueryFilters().ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, "acknowledged"));
        }
        await deletion.DeleteAsync(user, default);
        await using var check = new DeyeSolarDbContext(fixture.Options);
        Assert.Equal("other", (await check.Users.SingleAsync()).Id);
        Assert.False(await check.Installations.AnyAsync(i => i.Id == "installation"));
        Assert.True(await check.Installations.AnyAsync(i => i.Id == "neighbour"));
        Assert.Equal("other", (await check.InstallationMemberships.SingleAsync()).UserId);
        Assert.Empty(await check.IntegrationCommands.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await check.ExportFeedPrices.IgnoreQueryFilters().ToListAsync());
    }
    [SqlServerFact]
    public async Task CanceledDeletionRestoresAdmissionAndStartupRecoveryClearsOnlyRecordedFences()
    {
        await using var fixture = await Fixture.StartAsync();
        using var cancellation = new CancellationTokenSource();
        var options = new DbContextOptionsBuilder<DeyeSolarDbContext>(fixture.Options).AddInterceptors(new CancelAdmissionCommit(cancellation)).Options;
        await using var registry = Registry();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AccountDeletionService(options, registry)
            .DeleteAsync(new IdentityUser { Id = "owner", SecurityStamp = "stamp" }, cancellation.Token));
        await using (var db = new DeyeSolarDbContext(fixture.Options))
        {
            var installation = await db.Installations.SingleAsync(i => i.Id == "installation");
            Assert.True(installation.IsEnabled); Assert.Null(installation.OffboardingUserId); Assert.Null(installation.OffboardingWasEnabled);
            Assert.True(await db.Users.AnyAsync(u => u.Id == "owner" && u.SecurityStamp == "stamp"));
            // Simulate a process crash after a committed temporary admission fence.
            installation.OffboardingUserId = "owner"; installation.OffboardingWasEnabled = true; installation.IsEnabled = false;
            db.Installations.Add(new Installation { Id = "disabled-by-owner", IsEnabled = false });
            await db.SaveChangesAsync();
        }
        var recovery = new AccountOffboardingRecovery(fixture.Options);
        Assert.Equal(1, await recovery.RecoverInterruptedAsync()); Assert.Equal(0, await recovery.RecoverInterruptedAsync());
        await using var check = new DeyeSolarDbContext(fixture.Options);
        Assert.True((await check.Installations.SingleAsync(i => i.Id == "installation")).IsEnabled);
        Assert.False((await check.Installations.SingleAsync(i => i.Id == "disabled-by-owner")).IsEnabled);
        Assert.True(await check.Users.AnyAsync(u => u.Id == "owner"));
    }
    private sealed class MutationProbe : IConfigurationRules
    {
        public int Mutations { get; private set; }
        public Task<List<TriggerRule>> GetAllAsync(CancellationToken ct) => Task.FromResult(new List<TriggerRule>());
        public Task<TriggerRule?> GetByIdAsync(int id, CancellationToken ct) => Task.FromResult<TriggerRule?>(null);
        public Task<TriggerRule> CreateAsync(TriggerRule rule, CancellationToken ct) { Mutations++; return Task.FromResult(rule); }
        public Task UpdateAsync(TriggerRule rule, CancellationToken ct) { Mutations++; return Task.CompletedTask; }
        public Task DeleteAsync(int id, string configurationVersion, CancellationToken ct) { Mutations++; return Task.CompletedTask; }
    }
    private sealed class CancelAdmissionCommit(CancellationTokenSource cancellation) : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
        { cancellation.Cancel(); return Task.CompletedTask; }
    }
    private static TenantRuntimeRegistry Registry() => new((_, _) => throw new InvalidOperationException("This test must not create runtime polling."),
        _ => Task.FromResult<IReadOnlyList<string>>([]), CancellationToken.None);
    private static ClaimsPrincipal Actor(IdentityUser user, MobileSession session) => new(new ClaimsIdentity(new[] {
        new Claim(ClaimTypes.NameIdentifier, user.Id), new Claim(InstallationAccessAuthorizer.StampClaim, user.SecurityStamp!),
        new Claim(InstallationAccessAuthorizer.SessionClaim, session.Token), new Claim(InstallationIds.ClaimType, "installation") }, IdentityConstants.ApplicationScheme));
    private sealed class NoDelivery : IIdentityVerificationDelivery
    {
        public Task SendEmailAsync(string destination, string code, CancellationToken ct) => Task.CompletedTask;
        public Task SendPhoneAsync(string destination, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> CheckPhoneAsync(string destination, string code, CancellationToken ct) => Task.FromResult(false);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Fixture(DbContextOptions<DeyeSolarDbContext> options, SqlServerTestDatabase database) : IAsyncDisposable
    {
        public DbContextOptions<DeyeSolarDbContext> Options => options;
        public Clock Clock { get; } = new();
        public static async Task<Fixture> StartAsync()
        {
            var database = await SqlServerTestDatabase.CreateAsync("SolarSecurity");
            var fixture = new Fixture(database.Options, database);
            try
            {
                await using var db = new DeyeSolarDbContext(database.Options);
                db.Users.Add(new IdentityUser { Id = "owner", UserName = "owner", NormalizedUserName = "OWNER", SecurityStamp = "stamp" });
                db.Installations.Add(new Installation { Id = "installation", CreatedAt = DateTimeOffset.UtcNow });
                db.InstallationMemberships.Add(new() { UserId = "owner", InstallationId = "installation", Role = "Owner" });
                await db.SaveChangesAsync();
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        public ServiceProvider Services(IAccountSessionStore store)
        {
            var services = new ServiceCollection(); services.AddLogging(); services.AddHttpContextAccessor();
            services.AddSingleton(options); services.AddScoped(_ => new DeyeSolarDbContext(options));
            services.AddIdentity<IdentityUser, IdentityRole>(o => { o.Password.RequiredLength = 12; }).AddEntityFrameworkStores<DeyeSolarDbContext>().AddDefaultTokenProviders();
            services.AddSingleton(store); services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton(new AuthProviderOptions()); services.AddSingleton<IIdentityVerificationDelivery, NoDelivery>();
            services.AddSingleton<OneTimeVerificationService>(); services.AddSingleton(Registry()); services.AddAccountSecurity();
            return services.BuildServiceProvider();
        }
        public ValueTask DisposeAsync() => database.DisposeAsync();
    }
}
