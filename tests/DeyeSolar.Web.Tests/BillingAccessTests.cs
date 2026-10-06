using DeyeSolar.Web.Billing;
using DeyeSolar.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Tests;

public class BillingAccessTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    [Fact]
    public async Task VerifiedRenewalDisclosureDoesNotChangeAccessDeadlineAndStaleDetailsDisappear()
    {
        await using var fixture = await Fixture.CreateAsync(); await fixture.ExpireTrialAsync();
        await fixture.SubscriptionAsync(AppleSubscriptionStatus.Active);
        await using (var db = new DeyeSolarDbContext(fixture.Options))
        {
            var row = await db.AppleSubscriptions.SingleAsync(); row.AutoRenewEnabled = true; row.RenewalAt = Now.AddDays(10); await db.SaveChangesAsync();
        }
        var access = await fixture.Service.ReadAsync("one");
        Assert.Equal("com.dshapar.solar.monthly", access.ProductId); Assert.Equal("month", access.PlanPeriod);
        Assert.True(access.AutoRenewEnabled); Assert.Equal(Now.AddDays(10), access.RenewalAt);
        Assert.Equal(Now.AddHours(1), access.AccessValidUntil); Assert.Equal(0, access.TrialDaysRemaining);
        fixture.Clock.Now = Now.AddHours(1);
        var stale = await fixture.Service.ReadAsync("one"); Assert.Null(stale.ProductId); Assert.Null(stale.PlanPeriod); Assert.Null(stale.AutoRenewEnabled); Assert.Null(stale.RenewalAt);
    }

    [Theory]
    [InlineData(2026, 1, 31, 2026, 2, 28)]
    [InlineData(2028, 1, 31, 2028, 2, 29)]
    [InlineData(2026, 12, 15, 2027, 1, 15)]
    public void TrialUsesOneUtcCalendarMonth(int year, int month, int day, int endYear, int endMonth, int endDay)
    {
        var account = BillingAccount.Create("one", new(year, month, day, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(endYear, endMonth, endDay, 12, 0, 0, TimeSpan.Zero), account.TrialEndsAt);
        Assert.NotEqual(Guid.Empty, account.AppAccountToken);
        Assert.NotEqual(account.AppAccountToken, BillingAccount.Create("two", Now).AppAccountToken);
    }

    [Fact]
    public async Task TrialEndsAtExactBoundaryWithoutRestartOrResettingItsAccountToken()
    {
        await using var f = await Fixture.CreateAsync();
        var before = await f.Service.ReadAsync("one");
        Assert.Equal("trial", before.Status);
        Assert.Equal(1, before.SocketLimit);
        Assert.True(before.HasAccess);
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero), before.AccessValidUntil);
        f.Clock.Now = before.TrialEndsAt;
        var ended = await f.Service.ReadAsync("one");
        Assert.False(ended.HasAccess);
        Assert.Equal("expired", ended.Status);
        Assert.Null(ended.AccessValidUntil);
        Assert.Equal(before.AppAccountToken, ended.AppAccountToken);
        await using var fresh = new DeyeSolarDbContext(f.Options);
        Assert.Equal(before.TrialEndsAt, (await fresh.BillingAccounts.SingleAsync(a => a.UserId == "one")).TrialEndsAt);
    }

    [Fact]
    public async Task PaidSubscriptionRestoresAccessOnlyToItsBoundAccountAndAllowsMoreSockets()
    {
        await using var f = await Fixture.CreateAsync();
        await f.ExpireTrialAsync();
        var other = await f.Service.ReadAsync("two");
        await f.SubscriptionAsync(AppleSubscriptionStatus.Active);
        var paid = await f.Service.ReadAsync("one");
        Assert.Equal("active", paid.Status);
        Assert.Null(paid.SocketLimit);
        Assert.True(paid.HasAccess);
        Assert.Equal(Now.AddDays(10), paid.SubscriptionExpiresAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 13, 0, 0, TimeSpan.Zero), paid.AccessValidUntil);
        Assert.Equal(other, await f.Service.ReadAsync("two"));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("retry")]
    [InlineData("stale")]
    [InlineData("foreign_token")]
    [InlineData("foreign_product")]
    [InlineData("foreign_environment")]
    [InlineData("future_checked")]
    [InlineData("free_trial")]
    [InlineData("invalidated")]
    [InlineData("revoked_status")]
    public async Task UntrustedOrNonEntitledSubscriptionCannotUnlockExpiredTrial(string failure)
    {
        await using var f = await Fixture.CreateAsync();
        await f.ExpireTrialAsync();
        await f.SubscriptionAsync(AppleSubscriptionStatus.Active);
        await using (var db = new DeyeSolarDbContext(f.Options))
        {
            var row = await db.AppleSubscriptions.SingleAsync();
            switch (failure)
            {
                case "expired": row.ExpiresAt = Now; break;
                case "revoked": row.RevokedAt = Now.AddSeconds(-1); break;
                case "retry": row.Status = AppleSubscriptionStatus.BillingRetry; break;
                case "stale": row.CheckedAt = Now.AddHours(-1); break;
                case "foreign_token": row.AppAccountToken = Guid.NewGuid(); break;
                case "foreign_product": row.ProductId = "unknown"; break;
                case "foreign_environment": row.Environment = "Sandbox"; break;
                case "future_checked": row.CheckedAt = Now.AddMinutes(1); break;
                case "free_trial": row.IsFreeTrial = true; break;
                case "invalidated": row.InvalidatedAt = Now; break;
                case "revoked_status": row.Status = AppleSubscriptionStatus.Revoked; break;
            }
            await db.SaveChangesAsync();
        }
        var denied = await f.Service.ReadAsync("one");
        Assert.False(denied.HasAccess);
        Assert.Null(denied.AccessValidUntil);
    }

    [Fact]
    public async Task AuthoritativeGracePeriodSurvivesTransactionExpiryThenClosesAtItsOwnBoundary()
    {
        await using var f = await Fixture.CreateAsync();
        await f.ExpireTrialAsync();
        await f.SubscriptionAsync(AppleSubscriptionStatus.BillingGracePeriod);
        var grace = await f.Service.ReadAsync("one");
        Assert.True(grace.HasAccess);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 12, 30, 0, TimeSpan.Zero), grace.AccessValidUntil);
        f.Clock.Now = Now.AddMinutes(30);
        Assert.False((await f.Service.ReadAsync("one")).HasAccess);
    }

    [Fact]
    public void OldTrustedPaidGrantHasTenSecondsOfAccessEvenWhenItsActualExpirationIsNextYear()
    {
        var subscription = new AppleSubscription
        {
            Status = AppleSubscriptionStatus.Active,
            CheckedAt = new DateTimeOffset(2026, 10, 4, 11, 0, 10, TimeSpan.Zero),
            ExpiresAt = new DateTimeOffset(2027, 10, 4, 12, 0, 0, TimeSpan.Zero)
        };
        var cutoff = new DateTimeOffset(2026, 10, 4, 12, 0, 10, TimeSpan.Zero);
        Assert.Equal(cutoff, BillingAccessService.PaidAccessValidUntil(subscription, Now));
        Assert.True(BillingAccessService.HasPaidAccess(subscription, cutoff.AddTicks(-1)));
        Assert.Null(BillingAccessService.PaidAccessValidUntil(subscription, cutoff));
        Assert.False(BillingAccessService.HasPaidAccess(subscription, cutoff));
        Assert.Equal(new DateTimeOffset(2027, 10, 4, 12, 0, 0, TimeSpan.Zero), subscription.ExpiresAt);
    }

    [Fact]
    public async Task PaidCacheDeadlineIsExposedWithoutChangingActualExpirationOrNeighbourAccess()
    {
        await using var f = await Fixture.CreateAsync();
        await f.ExpireTrialAsync();
        await f.SubscriptionAsync(AppleSubscriptionStatus.Active);
        var neighbour = await f.Service.ReadAsync("two");
        await using (var db = new DeyeSolarDbContext(f.Options))
        {
            var subscription = await db.AppleSubscriptions.SingleAsync();
            subscription.CheckedAt = new DateTimeOffset(2026, 10, 4, 11, 0, 10, TimeSpan.Zero);
            subscription.ExpiresAt = new DateTimeOffset(2027, 10, 4, 12, 0, 0, TimeSpan.Zero);
            await db.SaveChangesAsync();
        }
        var active = await f.Service.ReadAsync("one");
        Assert.Equal("active", active.Status);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 12, 0, 10, TimeSpan.Zero), active.AccessValidUntil);
        Assert.Equal(new DateTimeOffset(2027, 10, 4, 12, 0, 0, TimeSpan.Zero), active.SubscriptionExpiresAt);
        Assert.Equal(neighbour, await f.Service.ReadAsync("two"));
        f.Clock.Now = new DateTimeOffset(2026, 10, 4, 12, 0, 10, TimeSpan.Zero);
        var expired = await f.Service.ReadAsync("one");
        Assert.False(expired.HasAccess);
        Assert.Null(expired.AccessValidUntil);
        Assert.Null(expired.SubscriptionExpiresAt);
        var stillIndependent = await f.Service.ReadAsync("two");
        Assert.True(stillIndependent.HasAccess);
        Assert.Equal(neighbour.AppAccountToken, stillIndependent.AppAccountToken);
        Assert.Equal(neighbour.AccessValidUntil, stillIndependent.AccessValidUntil);
    }

    [Fact]
    public async Task PaidExpirationAndRefundCannotShortenRemainingTrialAndTrialQuotaResumes()
    {
        await using var f = await Fixture.CreateAsync();
        await f.SubscriptionAsync(AppleSubscriptionStatus.Active);
        var neighbour = await f.Service.ReadAsync("two");
        await using (var db = new DeyeSolarDbContext(f.Options))
        {
            var subscription = await db.AppleSubscriptions.SingleAsync();
            subscription.CheckedAt = new DateTimeOffset(2026, 10, 4, 11, 0, 10, TimeSpan.Zero);
            subscription.ExpiresAt = new DateTimeOffset(2026, 10, 4, 12, 0, 5, TimeSpan.Zero);
            await db.SaveChangesAsync();
        }
        var paid = await f.Service.ReadAsync("one");
        Assert.Equal("active", paid.Status);
        Assert.Null(paid.SocketLimit);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 12, 0, 5, TimeSpan.Zero), paid.SubscriptionExpiresAt);
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero), paid.AccessValidUntil);
        f.Clock.Now = new DateTimeOffset(2026, 10, 4, 12, 0, 5, TimeSpan.Zero);
        var trial = await f.Service.ReadAsync("one");
        Assert.Equal("trial", trial.Status);
        Assert.True(trial.HasAccess);
        Assert.Equal(1, trial.SocketLimit);
        Assert.Equal(paid.AccessValidUntil, trial.AccessValidUntil);
        await using (var db = new DeyeSolarDbContext(f.Options))
        {
            var subscription = await db.AppleSubscriptions.SingleAsync();
            subscription.Status = AppleSubscriptionStatus.Revoked;
            subscription.RevokedAt = f.Clock.Now;
            await db.SaveChangesAsync();
        }
        Assert.Equal(trial, await f.Service.ReadAsync("one"));
        var stillIndependent = await f.Service.ReadAsync("two");
        Assert.True(stillIndependent.HasAccess);
        Assert.Equal(neighbour.AppAccountToken, stillIndependent.AppAccountToken);
        Assert.Equal(neighbour.AccessValidUntil, stillIndependent.AccessValidUntil);
    }

    [Fact]
    public async Task MostDurableVerifiedGrantWinsInsteadOfSubscriptionWithLatestActualExpiration()
    {
        await using var f = await Fixture.CreateAsync();
        await f.ExpireTrialAsync();
        await f.SubscriptionAsync(AppleSubscriptionStatus.Active);
        await using (var db = new DeyeSolarDbContext(f.Options))
        {
            var shortTrust = await db.AppleSubscriptions.SingleAsync();
            shortTrust.CheckedAt = new DateTimeOffset(2026, 10, 4, 11, 0, 10, TimeSpan.Zero);
            shortTrust.ExpiresAt = new DateTimeOffset(2027, 10, 4, 12, 0, 0, TimeSpan.Zero);
            db.AppleSubscriptions.Add(new()
            {
                OriginalTransactionId = "other-original",
                TransactionId = "other-transaction",
                UserId = "one",
                AppAccountToken = shortTrust.AppAccountToken,
                Environment = "Production",
                ProductId = shortTrust.ProductId,
                Status = AppleSubscriptionStatus.Active,
                ExpiresAt = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero),
                CheckedAt = new DateTimeOffset(2026, 10, 4, 11, 59, 30, TimeSpan.Zero)
            });
            await db.SaveChangesAsync();
        }
        var active = await f.Service.ReadAsync("one");
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 12, 59, 30, TimeSpan.Zero), active.AccessValidUntil);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero), active.SubscriptionExpiresAt);
        f.Clock.Now = new DateTimeOffset(2026, 10, 4, 12, 0, 10, TimeSpan.Zero);
        Assert.Equal("active", (await f.Service.ReadAsync("one")).Status);
        f.Clock.Now = new DateTimeOffset(2026, 10, 4, 12, 59, 30, TimeSpan.Zero);
        var expired = await f.Service.ReadAsync("one");
        Assert.False(expired.HasAccess);
        Assert.Null(expired.AccessValidUntil);
    }

    [Fact]
    public async Task TrustedPaidGrantCanOutlastShortRemainingTrialWithoutExtendingEitherSource()
    {
        await using var f = await Fixture.CreateAsync();
        await f.SubscriptionAsync(AppleSubscriptionStatus.Active);
        await using (var db = new DeyeSolarDbContext(f.Options))
        {
            (await db.BillingAccounts.SingleAsync(a => a.UserId == "one")).TrialStartedAt
                = new DateTimeOffset(2026, 9, 4, 12, 0, 5, TimeSpan.Zero);
            var paid = await db.AppleSubscriptions.SingleAsync();
            paid.CheckedAt = new DateTimeOffset(2026, 10, 4, 11, 0, 10, TimeSpan.Zero);
            paid.ExpiresAt = new DateTimeOffset(2027, 10, 4, 12, 0, 0, TimeSpan.Zero);
            await db.SaveChangesAsync();
        }
        var active = await f.Service.ReadAsync("one");
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 12, 0, 5, TimeSpan.Zero), active.TrialEndsAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 12, 0, 10, TimeSpan.Zero), active.AccessValidUntil);
        f.Clock.Now = new DateTimeOffset(2026, 10, 4, 12, 0, 5, TimeSpan.Zero);
        Assert.Equal("active", (await f.Service.ReadAsync("one")).Status);
        f.Clock.Now = new DateTimeOffset(2026, 10, 4, 12, 0, 10, TimeSpan.Zero);
        Assert.False((await f.Service.ReadAsync("one")).HasAccess);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = BillingAccessTests.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture(SqliteConnection connection, DbContextOptions<DeyeSolarDbContext> options) : IAsyncDisposable
    {
        public DbContextOptions<DeyeSolarDbContext> Options => options;
        public Clock Clock { get; } = new();
        public BillingAccessService Service => new(options, Clock, new AppleBillingOptions { Enabled = true });
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            connection.CreateCollation("Latin1_General_100_BIN2", string.CompareOrdinal);
            var options = new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlite(connection).Options;
            await using var db = new DeyeSolarDbContext(options);
            await db.Database.EnsureCreatedAsync();
            db.Users.AddRange(new IdentityUser { Id = "one", UserName = "one" }, new IdentityUser { Id = "two", UserName = "two" });
            await db.SaveChangesAsync();
            foreach (var account in await db.BillingAccounts.ToListAsync())
            {
                account.TrialStartedAt = Now;
            }
            await db.SaveChangesAsync();
            return new(connection, options);
        }
        public async Task ExpireTrialAsync()
        {
            await using var db = new DeyeSolarDbContext(options);
            (await db.BillingAccounts.SingleAsync(a => a.UserId == "one")).TrialStartedAt = Now.AddMonths(-1);
            await db.SaveChangesAsync();
        }
        public async Task SubscriptionAsync(AppleSubscriptionStatus status)
        {
            await using var db = new DeyeSolarDbContext(options);
            var account = await db.BillingAccounts.SingleAsync(a => a.UserId == "one");
            db.AppleSubscriptions.Add(new()
            {
                OriginalTransactionId = "original",
                UserId = "one",
                AppAccountToken = account.AppAccountToken,
                ProductId = "com.dshapar.solar.monthly",
                Environment = "Production",
                Status = status,
                TransactionId = "transaction",
                ExpiresAt = status == AppleSubscriptionStatus.BillingGracePeriod ? Now.AddDays(-1) : Now.AddDays(10),
                GracePeriodExpiresAt = status == AppleSubscriptionStatus.BillingGracePeriod ? Now.AddMinutes(30) : null,
                CheckedAt = Now,
                SourceSignedAt = Now,
                ObservationStartedAt = Now
            });
            await db.SaveChangesAsync();
        }
        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}
