using System.Data;
using DeyeSolar.Web.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Billing;

public sealed class AppleBillingService(DbContextOptions<DeyeSolarDbContext> databaseOptions,
    IAppleSignedDataVerifier verifier, IAppleAppStoreClient apple, BillingAccessService access, AppleBillingOptions options,
    TimeProvider clock)
{
    public async Task<BillingAccess> VerifyPurchaseAsync(string userId, string signedTransaction, CancellationToken cancellationToken)
    {
        var transaction = verifier.VerifyTransaction(signedTransaction);
        // Resolve the account from the authenticated principal before any Apple server request.
        var account = await access.ReadAsync(userId, cancellationToken);
        if (transaction.AppAccountToken != account.AppAccountToken) throw AccountMismatch();
        await RefreshAsync(userId, transaction.AppAccountToken, transaction.OriginalTransactionId, cancellationToken);
        return await access.ReadAsync(userId, cancellationToken);
    }

    public async Task HandleNotificationAsync(string signedPayload, CancellationToken cancellationToken)
    {
        var notification = verifier.VerifyNotification(signedPayload);
        if (notification.Transaction is not { } transaction) return;
        await using var db = new DeyeSolarDbContext(databaseOptions);
        var userId = await db.Set<BillingAccount>().Where(account => account.AppAccountToken == transaction.AppAccountToken)
            .Select(account => account.UserId).SingleOrDefaultAsync(cancellationToken);
        // Unknown or deleted accounts have no entitlement to update. Do not create them from Apple callbacks.
        if (userId is null) return;
        // Notifications invalidate cached data; even duplicate and old events refresh the authoritative API.
        await RefreshAsync(userId, transaction.AppAccountToken, transaction.OriginalTransactionId, cancellationToken);
    }

    public async Task RefreshAsync(string userId, Guid appAccountToken, string originalTransactionId, CancellationToken cancellationToken)
    {
        if (!options.Enabled) throw new AppleBillingException("Apple subscriptions are not configured.", "apple_unavailable", true);
        AppleSubscriptionObservation observation;
        try
        {
            observation = await apple.ReadSubscriptionAsync(originalTransactionId, cancellationToken);
            if (observation.Transaction.AppAccountToken != appAccountToken
                || observation.Transaction.OriginalTransactionId != originalTransactionId
                || observation.Transaction.Environment != options.Environment
                || !options.ProductIds.Contains(observation.Transaction.ProductId, StringComparer.Ordinal))
                throw new AppleStatusInvalidException(observation.StartedAt);
        }
        catch (AppleStatusInvalidException exception)
        {
            await RetryWriteAsync(() => InvalidateAsync(userId, appAccountToken, originalTransactionId,
                exception.ObservationStartedAt, cancellationToken), cancellationToken);
            throw;
        }
        await RetryWriteAsync(() => PersistAsync(userId, appAccountToken, observation, cancellationToken), cancellationToken);
    }

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

    private async Task PersistAsync(string userId, Guid token, AppleSubscriptionObservation observation, CancellationToken cancellationToken)
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
        await db.SaveChangesAsync(cancellationToken);
        await atomic.CommitAsync(cancellationToken);
    }

    private async Task InvalidateAsync(string userId, Guid token, string originalTransactionId,
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
