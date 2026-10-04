using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using DeyeSolar.Web.Api;
using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Billing;
using DeyeSolar.Web.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeyeSolar.Web.Tests;

public class AppleBillingSqlServerTests
{
    private const string UserA = "apple-owner-a";
    private const string UserB = "apple-owner-b";
    private const string Password = "Local subscription password 42!";

    [SqlServerFact]
    public async Task AuthenticatedPurchaseAndConcurrentReplayCreateOneBoundSubscriptionWithoutChangingNeighbour()
    {
        await using var host = await Host.StartAsync();
        var before = await host.NeighbourAsync();
        using (var anonymous = await host.Client.PostAsJsonAsync("/api/billing/apple/verify", new AppleVerifyRequest(host.Receipt())))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(0, host.Apple.Calls);
        using var client = await host.LoginAsync(UserA);
        var access = await client.GetFromJsonAsync<BillingAccess>("/api/billing/access");
        Assert.Equal("expired", access!.Status);
        Assert.Equal(host.Signer.Token, access.AppAccountToken);
        var receipt = host.Receipt();
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => client.PostAsJsonAsync(
            "/api/billing/apple/verify", new AppleVerifyRequest(receipt))));
        foreach (var response in responses)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var paid = (await response.Content.ReadFromJsonAsync<BillingAccess>())!;
                Assert.Equal("active", paid.Status);
                Assert.True(paid.HasAccess);
                Assert.Null(paid.SocketLimit);
                Assert.Equal(host.Apple.ExpiresAt, paid.SubscriptionExpiresAt);
            }
        }
        await using var check = host.Context();
        var subscription = Assert.Single(await check.AppleSubscriptions.Where(row => row.UserId == UserA).AsNoTracking().ToListAsync());
        Assert.Equal("1001", subscription.OriginalTransactionId);
        Assert.Equal(host.Signer.Token, subscription.AppAccountToken);
        Assert.Null(subscription.InvalidatedAt);
        Assert.Equal(before, await host.NeighbourAsync());
    }

    [SqlServerFact]
    public async Task ForgedClientReceiptAndCrossAccountRestoreCannotInvalidateAnyTrustedSubscription()
    {
        await using var host = await Host.StartAsync();
        using var own = await host.LoginAsync(UserA);
        using (var purchase = await own.PostAsJsonAsync("/api/billing/apple/verify", new AppleVerifyRequest(host.Receipt())))
            Assert.Equal(HttpStatusCode.OK, purchase.StatusCode);
        var before = await host.StateAsync();
        var calls = host.Apple.Calls;
        using (var forged = await own.PostAsJsonAsync("/api/billing/apple/verify", new AppleVerifyRequest("forged.receipt.signature")))
            Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        using var foreign = await host.LoginAsync(UserB);
        using (var restore = await foreign.PostAsJsonAsync("/api/billing/apple/verify", new AppleVerifyRequest(host.Receipt())))
            Assert.Equal(HttpStatusCode.Conflict, restore.StatusCode);
        Assert.Equal(calls, host.Apple.Calls);
        Assert.Equal(before, await host.StateAsync());
        using (var access = await own.GetAsync("/api/billing/access")) Assert.Equal(HttpStatusCode.OK, access.StatusCode);
    }

    [SqlServerFact]
    public async Task NotificationRefundGraceExpirationAndBillingRetryUseAuthoritativeStatusWithoutExtendingTrial()
    {
        await using var host = await Host.StartAsync();
        using var own = await host.LoginAsync(UserA);
        var neighbour = await host.NeighbourAsync();
        using (var purchase = await own.PostAsJsonAsync("/api/billing/apple/verify", new AppleVerifyRequest(host.Receipt())))
            Assert.Equal(HttpStatusCode.OK, purchase.StatusCode);
        var trialEndsAt = (await own.GetFromJsonAsync<BillingAccess>("/api/billing/access"))!.TrialEndsAt;
        host.Clock.Now += TimeSpan.FromSeconds(1);
        host.Apple.Status = AppleSubscriptionStatus.Revoked;
        host.Apple.RevokedAt = host.Clock.Now;
        using (var refund = await host.NotifyAsync()) Assert.Equal(HttpStatusCode.OK, refund.StatusCode);
        Assert.False((await own.GetFromJsonAsync<BillingAccess>("/api/billing/access"))!.HasAccess);
        var revoked = await host.OwnAsync();
        Assert.Equal(AppleSubscriptionStatus.Revoked, revoked.Status);
        Assert.Equal(host.Clock.Now, revoked.RevokedAt);
        host.Clock.Now += TimeSpan.FromSeconds(1);
        host.Apple.Status = AppleSubscriptionStatus.BillingGracePeriod;
        host.Apple.RevokedAt = null;
        host.Apple.ExpiresAt = host.Clock.Now.AddDays(-1);
        host.Apple.GraceExpiresAt = host.Clock.Now.AddMinutes(20);
        using (var grace = await host.NotifyAsync()) Assert.Equal(HttpStatusCode.OK, grace.StatusCode);
        var access = (await own.GetFromJsonAsync<BillingAccess>("/api/billing/access"))!;
        Assert.True(access.HasAccess);
        Assert.Equal(host.Apple.GraceExpiresAt, access.SubscriptionExpiresAt);
        host.Clock.Now = host.Apple.GraceExpiresAt.Value;
        Assert.False((await own.GetFromJsonAsync<BillingAccess>("/api/billing/access"))!.HasAccess);
        host.Clock.Now += TimeSpan.FromSeconds(1);
        host.Apple.Status = AppleSubscriptionStatus.BillingRetry;
        using (var retry = await host.NotifyAsync()) Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        access = (await own.GetFromJsonAsync<BillingAccess>("/api/billing/access"))!;
        Assert.False(access.HasAccess);
        Assert.Equal(trialEndsAt, access.TrialEndsAt);
        Assert.Equal(neighbour, await host.NeighbourAsync());
    }

    [SqlServerFact]
    public async Task DelayedOlderActiveResponseAndOlderSignedStatusCannotUndoNewerRefundOrRefreshItsAge()
    {
        await using var host = await Host.StartAsync();
        using var own = await host.LoginAsync(UserA);
        var pause = host.Apple.PauseNext();
        var delayed = own.PostAsJsonAsync("/api/billing/apple/verify", new AppleVerifyRequest(host.Receipt()));
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        host.Clock.Now += TimeSpan.FromSeconds(1);
        host.Apple.Status = AppleSubscriptionStatus.Revoked;
        host.Apple.RevokedAt = host.Clock.Now;
        using (var refund = await host.NotifyAsync()) Assert.Equal(HttpStatusCode.OK, refund.StatusCode);
        var winner = await host.OwnAsync();
        pause.Release.TrySetResult();
        using (var old = await delayed)
        {
            Assert.Equal(HttpStatusCode.OK, old.StatusCode);
            Assert.False((await old.Content.ReadFromJsonAsync<BillingAccess>())!.HasAccess);
        }
        Assert.Equal(JsonSerializer.Serialize(winner), JsonSerializer.Serialize(await host.OwnAsync()));
        host.Clock.Now += TimeSpan.FromSeconds(1);
        host.Apple.Status = AppleSubscriptionStatus.Active;
        host.Apple.RevokedAt = null;
        host.Apple.SignedAt = winner.SourceSignedAt.AddSeconds(-1);
        using (var stale = await host.NotifyAsync()) Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        Assert.Equal(JsonSerializer.Serialize(winner), JsonSerializer.Serialize(await host.OwnAsync()));
    }

    [SqlServerFact]
    public async Task InvalidAuthoritativeStatusClosesOnlyItsOwnerWhileTransientFailureRetainsBoundedTrustedState()
    {
        await using var host = await Host.StartAsync();
        using var own = await host.LoginAsync(UserA);
        using (var purchase = await own.PostAsJsonAsync("/api/billing/apple/verify", new AppleVerifyRequest(host.Receipt())))
            Assert.Equal(HttpStatusCode.OK, purchase.StatusCode);
        var before = await host.StateAsync();
        var neighbour = await host.NeighbourAsync();
        host.Apple.Failure = "transport";
        using (var outage = await host.NotifyAsync()) Assert.Equal(HttpStatusCode.ServiceUnavailable, outage.StatusCode);
        Assert.Equal(before, await host.StateAsync());
        Assert.True((await own.GetFromJsonAsync<BillingAccess>("/api/billing/access"))!.HasAccess);
        host.Clock.Now += TimeSpan.FromHours(1);
        Assert.False((await own.GetFromJsonAsync<BillingAccess>("/api/billing/access"))!.HasAccess);
        host.Apple.Failure = null;
        using (var restored = await host.NotifyAsync()) Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        foreach (var failure in new[] { "bundle", "environment", "token", "signature", "missing", "duplicate-revoked-active", "duplicate-active-revoked" })
        {
            host.Clock.Now += TimeSpan.FromSeconds(1);
            host.Apple.Failure = failure;
            using (var invalid = await host.NotifyAsync()) Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.NotNull((await host.OwnAsync()).InvalidatedAt);
            Assert.False((await own.GetFromJsonAsync<BillingAccess>("/api/billing/access"))!.HasAccess);
            Assert.Equal(neighbour, await host.NeighbourAsync());
            host.Clock.Now += TimeSpan.FromSeconds(1);
            host.Apple.Failure = null;
            using (var restored = await host.NotifyAsync()) Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
            Assert.Null((await host.OwnAsync()).InvalidatedAt);
            Assert.True((await own.GetFromJsonAsync<BillingAccess>("/api/billing/access"))!.HasAccess);
        }
    }

    [SqlServerFact]
    public async Task DelayedInvalidResponseCannotInvalidateLaterVerifiedRenewal()
    {
        await using var host = await Host.StartAsync();
        using var own = await host.LoginAsync(UserA);
        host.Apple.Failure = "missing";
        var pause = host.Apple.PauseNext();
        var delayed = own.PostAsJsonAsync("/api/billing/apple/verify", new AppleVerifyRequest(host.Receipt()));
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        host.Clock.Now += TimeSpan.FromSeconds(1);
        host.Apple.Failure = null;
        using (var renewal = await host.NotifyAsync()) Assert.Equal(HttpStatusCode.OK, renewal.StatusCode);
        var winner = await host.OwnAsync();
        pause.Release.TrySetResult();
        using (var invalid = await delayed) Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(JsonSerializer.Serialize(winner), JsonSerializer.Serialize(await host.OwnAsync()));
        Assert.True((await own.GetFromJsonAsync<BillingAccess>("/api/billing/access"))!.HasAccess);
    }

    [SqlServerFact]
    public async Task AppleFreeOfferCannotExpandServerTrialQuotaOrExtendItsMonthAndPaidRenewalUnlocksAccess()
        => await VerifyFreeOfferAsync(useLegacyZeroPrice: false);

    [SqlServerFact]
    public async Task LegacyZeroPriceTransactionCannotExpandTrialQuotaOrExtendItsMonthAndPaidRenewalUnlocksAccess()
        => await VerifyFreeOfferAsync(useLegacyZeroPrice: true);

    private static async Task VerifyFreeOfferAsync(bool useLegacyZeroPrice)
    {
        await using var host = await Host.StartAsync();
        await using (var db = host.Context())
        {
            var account = await db.BillingAccounts.SingleAsync(row => row.UserId == UserA);
            account.TrialStartedAt = host.Clock.Now;
            await db.SaveChangesAsync();
        }
        using var own = await host.LoginAsync(UserA);
        var original = (await own.GetFromJsonAsync<BillingAccess>("/api/billing/access"))!;
        host.Apple.IsFreeTrial = !useLegacyZeroPrice;
        host.Apple.Price = useLegacyZeroPrice ? 0 : null;
        using (var purchase = await own.PostAsJsonAsync("/api/billing/apple/verify", new AppleVerifyRequest(host.Receipt())))
        {
            Assert.Equal(HttpStatusCode.OK, purchase.StatusCode);
            var access = (await purchase.Content.ReadFromJsonAsync<BillingAccess>())!;
            Assert.Equal("trial", access.Status);
            Assert.Equal(1, access.SocketLimit);
            Assert.Equal(original.TrialEndsAt, access.TrialEndsAt);
        }
        Assert.True((await host.OwnAsync()).IsFreeTrial);
        host.Clock.Now = original.TrialEndsAt;
        host.Apple.ExpiresAt = host.Clock.Now.AddDays(10);
        using (var freeRenewal = await host.NotifyAsync()) Assert.Equal(HttpStatusCode.OK, freeRenewal.StatusCode);
        Assert.False((await own.GetFromJsonAsync<BillingAccess>("/api/billing/access"))!.HasAccess);
        host.Clock.Now += TimeSpan.FromSeconds(1);
        host.Apple.IsFreeTrial = false;
        host.Apple.Price = 4990;
        using (var paidRenewal = await host.NotifyAsync()) Assert.Equal(HttpStatusCode.OK, paidRenewal.StatusCode);
        var paid = (await own.GetFromJsonAsync<BillingAccess>("/api/billing/access"))!;
        Assert.Equal("active", paid.Status);
        Assert.True(paid.HasAccess);
        Assert.Null(paid.SocketLimit);
        Assert.False((await host.OwnAsync()).IsFreeTrial);
        Assert.Equal(original.TrialEndsAt, paid.TrialEndsAt);
    }

    [SqlServerFact]
    public async Task ReconciliationStartupAppliesRefundOnlyInItsEnvironmentAndStopCancelsPendingStatusWithoutEffects()
    {
        await using var host = await Host.StartAsync();
        using var own = await host.LoginAsync(UserA);
        using (var purchase = await own.PostAsJsonAsync("/api/billing/apple/verify", new AppleVerifyRequest(host.Receipt())))
            Assert.Equal(HttpStatusCode.OK, purchase.StatusCode);
        await using (var db = host.Context())
        {
            (await db.AppleSubscriptions.SingleAsync(subscription => subscription.UserId == UserB)).Environment = "Production";
            await db.SaveChangesAsync();
        }
        var neighbour = await host.NeighbourAsync();
        var before = await host.StateAsync();
        var calls = host.Apple.Calls;
        using (var disabled = host.Worker(enabled: false))
        {
            await disabled.StartAsync(default);
            await disabled.StopAsync(default);
        }
        Assert.Equal(calls, host.Apple.Calls);
        Assert.Equal(before, await host.StateAsync());

        host.Clock.Now += TimeSpan.FromSeconds(1);
        host.Apple.Status = AppleSubscriptionStatus.Revoked;
        host.Apple.RevokedAt = host.Clock.Now;
        using (var worker = host.Worker())
        {
            await worker.StartAsync(default);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while ((await host.OwnAsync()).Status != AppleSubscriptionStatus.Revoked)
                await Task.Delay(20, deadline.Token);
            await worker.StopAsync(deadline.Token);
        }
        Assert.Equal(calls + 1, host.Apple.Calls);
        Assert.False((await own.GetFromJsonAsync<BillingAccess>("/api/billing/access"))!.HasAccess);
        Assert.Equal(neighbour, await host.NeighbourAsync());

        host.Clock.Now += TimeSpan.FromSeconds(1);
        host.Apple.Status = AppleSubscriptionStatus.Active;
        host.Apple.RevokedAt = null;
        using (var restored = await host.NotifyAsync()) Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        before = await host.StateAsync();
        var pause = host.Apple.PauseNext();
        using (var worker = host.Worker())
        {
            await worker.StartAsync(default);
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await worker.StopAsync(deadline.Token);
            await pause.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        Assert.Equal(before, await host.StateAsync());
        Assert.Equal(neighbour, await host.NeighbourAsync());
    }

    [SqlServerFact]
    public async Task DatabaseSaveFailureRollsBackPurchaseAndCancellationBeforeAppleResponseHasNoEffects()
    {
        await using var host = await Host.StartAsync();
        using var own = await host.LoginAsync(UserA);
        var before = await host.StateAsync();
        host.DatabaseFailure.Enabled = true;
        using (var failed = await own.PostAsJsonAsync("/api/billing/apple/verify", new AppleVerifyRequest(host.Receipt())))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        host.DatabaseFailure.Enabled = false;
        Assert.Equal(before, await host.StateAsync());
        var pause = host.Apple.PauseNext();
        using var cancellation = new CancellationTokenSource();
        var pending = own.PostAsJsonAsync("/api/billing/apple/verify", new AppleVerifyRequest(host.Receipt()), cancellation.Token);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await pause.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(before, await host.StateAsync());
    }

    private sealed class Host(WebApplication app, HttpClient appleHttp, DbContextOptions<DeyeSolarDbContext> options,
        AppleSignedFixture signer, Clock clock, AppleHttp apple, DatabaseFailure databaseFailure, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public AppleSignedFixture Signer { get; } = signer;
        public Clock Clock { get; } = clock;
        public AppleHttp Apple { get; } = apple;
        public DatabaseFailure DatabaseFailure { get; } = databaseFailure;
        public DeyeSolarDbContext Context() => new(options);
        public AppleSubscriptionRefreshWorker Worker(bool enabled = true) => new(
            app.Services.GetRequiredService<IServiceScopeFactory>(), options, enabled ? Signer.Options : new AppleBillingOptions(),
            NullLogger<AppleSubscriptionRefreshWorker>.Instance);
        public string Receipt() => Signer.Sign(Signer.Transaction());
        public Task<HttpResponseMessage> NotifyAsync() => Client.PostAsJsonAsync("/api/billing/apple/notifications",
            new AppleNotificationRequest(Signer.Sign(Signer.Notification(Receipt()))));

        public async Task<HttpClient> LoginAsync(string userId)
        {
            using var response = await Client.PostAsJsonAsync("/_fixture/login", new MobileLoginRequest(userId, Password));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var session = (await response.Content.ReadFromJsonAsync<MobileSession>())!;
            var authenticated = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
            { BaseAddress = Client.BaseAddress, Timeout = TimeSpan.FromSeconds(30) };
            authenticated.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
            return authenticated;
        }

        public async Task<AppleSubscription> OwnAsync()
        {
            await using var db = Context();
            return await db.AppleSubscriptions.AsNoTracking().SingleAsync(subscription => subscription.UserId == UserA);
        }

        public async Task<string> StateAsync()
        {
            await using var db = Context();
            return JsonSerializer.Serialize(new
            {
                Accounts = await db.BillingAccounts.AsNoTracking().OrderBy(account => account.UserId).ToArrayAsync(),
                Subscriptions = await db.AppleSubscriptions.AsNoTracking().OrderBy(subscription => subscription.OriginalTransactionId).ToArrayAsync()
            });
        }

        public async Task<string> NeighbourAsync()
        {
            await using var db = Context();
            return JsonSerializer.Serialize(new
            {
                Account = await db.BillingAccounts.AsNoTracking().SingleAsync(account => account.UserId == UserB),
                Subscription = await db.AppleSubscriptions.AsNoTracking().SingleAsync(subscription => subscription.UserId == UserB)
            });
        }

        public static async Task<Host> StartAsync()
        {
            var signer = new AppleSignedFixture();
            var clock = new Clock(signer.Now);
            var databaseFailure = new DatabaseFailure();
            var connection = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SOLAR_TEST_SQL_CONNECTION"))
            { InitialCatalog = "SolarAppleBillingTests_" + Guid.NewGuid().ToString("N") };
            var options = new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlServer(connection.ConnectionString)
                .AddInterceptors(databaseFailure).Options;
            WebApplication? app = null;
            HttpClient? appleHttp = null;
            try
            {
                await using (var db = new DeyeSolarDbContext(options))
                {
                    await db.Database.MigrateAsync();
                    foreach (var id in new[] { UserA, UserB })
                    {
                        var installationId = id + "-site";
                        db.Installations.Add(new Installation { Id = installationId, CreatedAt = clock.Now });
                        var user = new IdentityUser
                        {
                            Id = id,
                            UserName = id,
                            NormalizedUserName = id.ToUpperInvariant(),
                            Email = id + "@example.test",
                            NormalizedEmail = (id + "@example.test").ToUpperInvariant(),
                            EmailConfirmed = true,
                            SecurityStamp = Guid.NewGuid().ToString()
                        };
                        user.PasswordHash = new PasswordHasher<IdentityUser>().HashPassword(user, Password);
                        db.Users.Add(user);
                        db.InstallationMemberships.Add(new InstallationMembership { UserId = id, InstallationId = installationId });
                        db.BillingAccounts.Add(new BillingAccount
                        {
                            UserId = id,
                            AppAccountToken = id == UserA ? signer.Token : Guid.NewGuid(),
                            TrialStartedAt = clock.Now.AddMonths(-2)
                        });
                    }
                    await db.SaveChangesAsync();
                    var neighbour = await db.BillingAccounts.SingleAsync(account => account.UserId == UserB);
                    db.AppleSubscriptions.Add(new AppleSubscription
                    {
                        OriginalTransactionId = "2001",
                        TransactionId = "2002",
                        UserId = UserB,
                        AppAccountToken = neighbour.AppAccountToken,
                        Environment = "Sandbox",
                        ProductId = signer.Options.ProductIds[0],
                        Status = AppleSubscriptionStatus.Active,
                        ExpiresAt = clock.Now.AddMonths(1),
                        CheckedAt = clock.Now,
                        ObservationStartedAt = clock.Now,
                        SourceSignedAt = clock.Now
                    });
                    await db.SaveChangesAsync();
                }
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = AppContext.BaseDirectory });
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Logging.ClearProviders();
                builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning);
                var factory = new Factory(options);
                builder.Services.AddSingleton(options);
                builder.Services.AddSingleton<IDbContextFactory<DeyeSolarDbContext>>(factory);
                builder.Services.AddScoped(_ => factory.CreateDbContext());
                builder.Services.AddIdentity<IdentityUser, IdentityRole>().AddEntityFrameworkStores<DeyeSolarDbContext>();
                builder.Services.AddAccountIdentities(new AuthProviderOptions());
                builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, MobileBearerAuthenticationHandler>(MobileBearerAuthenticationHandler.SchemeName, _ => { });
                builder.Services.AddAuthorization();
                builder.Services.AddHttpContextAccessor();
                builder.Services.AddSingleton<MobileSessionStore>();
                builder.Services.AddScoped<MobileAuthService>();
                builder.Services.AddSingleton<TimeProvider>(clock);
                builder.Services.AddSingleton<BillingAccessService>();
                builder.Services.AddScoped<CurrentBillingAccount>();
                builder.Services.AddAppleBilling(signer.Options);
                var worker = builder.Services.Single(descriptor => descriptor.ImplementationType == typeof(AppleSubscriptionRefreshWorker));
                builder.Services.Remove(worker);
                var verifier = new AppleSignedDataVerifier(signer.Options, clock, X509RevocationMode.NoCheck);
                var apple = new AppleHttp(signer, clock);
                appleHttp = new HttpClient(apple);
                builder.Services.AddSingleton<IAppleSignedDataVerifier>(verifier);
                builder.Services.AddSingleton<IAppleAppStoreClient>(new AppleAppStoreClient(appleHttp, signer.Options, verifier, clock));
                app = builder.Build();
                app.UseRouting();
                app.UseRateLimiter();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseMiddleware<BillingAccessMiddleware>();
                app.MapAppleBilling();
                app.MapPost("/_fixture/login", async Task<Microsoft.AspNetCore.Http.IResult> (MobileLoginRequest request, MobileAuthService auth) =>
                {
                    var session = await auth.SignInAsync(request);
                    return session is null ? Results.Unauthorized() : Results.Ok(session);
                }).AllowAnonymous();
                await app.StartAsync();
                var address = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses));
                Assert.Equal("127.0.0.1", address.Host);
                var client = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
                { BaseAddress = address, Timeout = TimeSpan.FromSeconds(30) };
                return new Host(app, appleHttp, options, signer, clock, apple, databaseFailure, client);
            }
            catch
            {
                if (app is not null) await app.DisposeAsync();
                appleHttp?.Dispose();
                signer.Dispose();
                await using var db = new DeyeSolarDbContext(options);
                await db.Database.EnsureDeletedAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            try { await app.StopAsync(); await app.DisposeAsync(); }
            finally
            {
                appleHttp.Dispose();
                Signer.Dispose();
                await using var db = Context();
                await db.Database.EnsureDeletedAsync();
            }
        }
    }

    private sealed class Factory(DbContextOptions<DeyeSolarDbContext> options) : IDbContextFactory<DeyeSolarDbContext>
    {
        public DeyeSolarDbContext CreateDbContext() => new(options);
        public Task<DeyeSolarDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(CreateDbContext()); }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class DatabaseFailure : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => Enabled ? throw new InvalidOperationException("Injected persistence failure before commit.") : ValueTask.FromResult(result);
    }

    private sealed class Pause
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // Apple's transport is synthetic; the HTTP endpoints, authentication, verifier, service and SQL are real.
    private sealed class AppleHttp(AppleSignedFixture signer, Clock clock) : HttpMessageHandler
    {
        private Pause? pause;
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public AppleSubscriptionStatus Status { get; set; } = AppleSubscriptionStatus.Active;
        public DateTimeOffset ExpiresAt { get; set; } = clock.Now.AddMonths(1);
        public DateTimeOffset? GraceExpiresAt { get; set; }
        public DateTimeOffset? RevokedAt { get; set; }
        public DateTimeOffset? SignedAt { get; set; }
        public bool IsFreeTrial { get; set; }
        public long? Price { get; set; }
        public string? Failure { get; set; }
        public Pause PauseNext() { var next = new Pause(); Interlocked.Exchange(ref pause, next); return next; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            if (Failure == "transport") throw new HttpRequestException("Injected Apple transport outage.");
            var response = Response();
            var pending = Interlocked.Exchange(ref pause, null);
            if (pending is not null)
            {
                pending.Entered.TrySetResult();
                try { await pending.Release.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { pending.Cancelled.TrySetResult(); throw; }
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) };
        }

        private string Response()
        {
            var transaction = signer.Transaction();
            transaction["expiresDate"] = ExpiresAt.ToUnixTimeMilliseconds();
            transaction["signedDate"] = (SignedAt ?? clock.Now).ToUnixTimeMilliseconds();
            if (RevokedAt is { } revokedAt) transaction["revocationDate"] = revokedAt.ToUnixTimeMilliseconds();
            if (IsFreeTrial) transaction["offerDiscountType"] = "FREE_TRIAL";
            if (Price is { } price) transaction["price"] = price;
            if (Failure == "token") transaction["appAccountToken"] = Guid.NewGuid().ToString();
            var renewal = new Dictionary<string, object>
            {
                ["originalTransactionId"] = "1001",
                ["productId"] = signer.Options.ProductIds[0],
                ["environment"] = "Sandbox",
                ["signedDate"] = (SignedAt ?? clock.Now).ToUnixTimeMilliseconds()
            };
            if (GraceExpiresAt is { } graceAt) renewal["gracePeriodExpiresDate"] = graceAt.ToUnixTimeMilliseconds();
            var json = JsonSerializer.Serialize(new
            {
                bundleId = Failure == "bundle" ? "foreign.app" : signer.Options.BundleId,
                environment = Failure == "environment" ? "Production" : "Sandbox",
                appAppleId = 0,
                data = Failure == "missing" ? [] : new[] { new { lastTransactions = new[] { new
                {
                    originalTransactionId = "1001", status = (int)Status,
                    signedTransactionInfo = Failure == "signature" ? "invalid.signed.transaction" : signer.Sign(transaction),
                    signedRenewalInfo = signer.Sign(renewal)
                } } } }
            });
            return Failure switch
            {
                "duplicate-revoked-active" => json.Replace("\"status\":1", "\"status\":5,\"status\":1", StringComparison.Ordinal),
                "duplicate-active-revoked" => json.Replace("\"status\":1", "\"status\":1,\"status\":5", StringComparison.Ordinal),
                _ => json
            };
        }
    }
}
