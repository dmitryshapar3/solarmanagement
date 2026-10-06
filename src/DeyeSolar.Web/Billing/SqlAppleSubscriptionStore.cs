using System.Data;
using DeyeSolar.Web.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Billing;

public sealed record AppleSubscriptionIdentity(string UserId, Guid AppAccountToken, string OriginalTransactionId);
public interface IAppleSubscriptionCatalog
{
    Task<string?> FindAccountAsync(Guid appAccountToken, CancellationToken ct);
    Task<IReadOnlyList<AppleSubscriptionIdentity>> ReadPageAsync(int offset, int count, CancellationToken ct);
}

public interface IAppleSubscriptionWriter
{
    Task PersistAsync(string userId, Guid appAccountToken, AppleSubscriptionObservation observation, CancellationToken ct);
    Task InvalidateAsync(string userId, Guid appAccountToken, string originalTransactionId, DateTimeOffset startedAt, CancellationToken ct);
}

/// <summary>Owns SQL atomicity, account binding and monotonic subscription observation writes.</summary>
public sealed class SqlAppleSubscriptionStore(DbContextOptions<DeyeSolarDbContext> databaseOptions,
    AppleBillingOptions options, TimeProvider clock) : IAppleSubscriptionCatalog, IAppleSubscriptionWriter
{
    public async Task<string?> FindAccountAsync(Guid appAccountToken, CancellationToken ct)
    {
        await using var db = new DeyeSolarDbContext(databaseOptions);
        return await db.BillingAccounts.Where(account => account.AppAccountToken == appAccountToken)
            .Select(account => account.UserId).SingleOrDefaultAsync(ct);
    }
    public async Task<IReadOnlyList<AppleSubscriptionIdentity>> ReadPageAsync(int offset, int count, CancellationToken ct)
    {
        if (offset < 0 || count is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(count));
        await using var db = new DeyeSolarDbContext(databaseOptions);
        return await db.AppleSubscriptions.AsNoTracking().Where(subscription => subscription.Environment == options.Environment)
            .OrderBy(subscription => subscription.OriginalTransactionId).Skip(offset).Take(count)
            .Select(subscription => new AppleSubscriptionIdentity(subscription.UserId, subscription.AppAccountToken, subscription.OriginalTransactionId))
            .ToListAsync(ct);
    }
    public Task PersistAsync(string userId, Guid appAccountToken, AppleSubscriptionObservation observation, CancellationToken ct)
        => RetryWriteAsync(() => PersistOnceAsync(userId, appAccountToken, observation, ct), ct);
    public Task InvalidateAsync(string userId, Guid appAccountToken, string originalTransactionId, DateTimeOffset startedAt, CancellationToken ct)
        => RetryWriteAsync(() => InvalidateOnceAsync(userId, appAccountToken, originalTransactionId, startedAt, ct), ct);

    private static async Task RetryWriteAsync(Func<Task> write, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await write();
                return;
            }
            catch (Exception exception) when (attempt < 2 && IsWriteConflict(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), cancellationToken);
            }
        }
    }

    private async Task PersistOnceAsync(string userId, Guid token, AppleSubscriptionObservation observation, CancellationToken cancellationToken)
    {
        await using var db = new DeyeSolarDbContext(databaseOptions);
        await using var atomic = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        if (!await db.Set<BillingAccount>().AnyAsync(account => account.UserId == userId && account.AppAccountToken == token, cancellationToken))
            throw AccountMismatch();
        var transaction = observation.Transaction;
        var subscription = await LockSubscriptionAsync(db, transaction.OriginalTransactionId, cancellationToken);
        if (subscription is not null)
        {
            if (subscription.UserId != userId || subscription.AppAccountToken != token
                || subscription.Environment != options.Environment) throw AccountMismatch();
            if (subscription.ObservationStartedAt > observation.StartedAt || subscription.SourceSignedAt > observation.SourceSignedAt
                || subscription.InvalidatedAt is not null && subscription.ObservationStartedAt == observation.StartedAt) return;
        }
        else
        {
            subscription = new AppleSubscription
            {
                OriginalTransactionId = transaction.OriginalTransactionId, UserId = userId, AppAccountToken = token,
                Environment = transaction.Environment
            };
            db.Set<AppleSubscription>().Add(subscription);
        }
        subscription.TransactionId = transaction.TransactionId;
        subscription.ProductId = transaction.ProductId;
        subscription.Status = observation.Status;
        subscription.ExpiresAt = transaction.ExpiresAt;
        subscription.GracePeriodExpiresAt = observation.GracePeriodExpiresAt;
        subscription.RevokedAt = transaction.RevokedAt;
        subscription.SourceSignedAt = observation.SourceSignedAt;
        subscription.ObservationStartedAt = observation.StartedAt;
        subscription.CheckedAt = observation.CheckedAt;
        subscription.InvalidatedAt = null;
        subscription.IsFreeTrial = transaction.IsFreeTrial;
        subscription.AutoRenewEnabled = observation.AutoRenewEnabled;
        subscription.RenewalAt = observation.RenewalAt;
        await db.SaveChangesAsync(cancellationToken);
        await atomic.CommitAsync(cancellationToken);
    }

    private async Task InvalidateOnceAsync(string userId, Guid token, string originalTransactionId,
        DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        await using var db = new DeyeSolarDbContext(databaseOptions);
        await using var atomic = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var subscription = await LockSubscriptionAsync(db, originalTransactionId, cancellationToken);
        // An invalid Apple response cannot affect another account or overwrite a later verified refresh.
        if (subscription is null || subscription.UserId != userId || subscription.AppAccountToken != token
            || subscription.Environment != options.Environment || subscription.ObservationStartedAt > startedAt) return;
        subscription.InvalidatedAt = clock.GetUtcNow();
        subscription.ObservationStartedAt = startedAt;
        await db.SaveChangesAsync(cancellationToken);
        await atomic.CommitAsync(cancellationToken);
    }

    private static Task<AppleSubscription?> LockSubscriptionAsync(DeyeSolarDbContext db, string originalTransactionId,
        CancellationToken cancellationToken) => (db.Database.IsSqlServer()
            ? db.Set<AppleSubscription>().FromSqlInterpolated($"SELECT * FROM [AppleSubscriptions] WITH (UPDLOCK, HOLDLOCK) WHERE [OriginalTransactionId] = {originalTransactionId}")
            : db.Set<AppleSubscription>().Where(subscription => subscription.OriginalTransactionId == originalTransactionId))
        .SingleOrDefaultAsync(cancellationToken);

    private static bool IsWriteConflict(Exception exception) => exception is DbUpdateConcurrencyException
        || exception is SqlException { Number: 1205 }
        || exception is DbUpdateException { InnerException: SqlException { Number: 1205 or 2601 or 2627 } };

    private static AppleBillingException AccountMismatch() => new("This Apple subscription belongs to a different Solar account.", "apple_account_mismatch");
}
