namespace DeyeSolar.Web.Billing;

public sealed class AppleBillingService(IAppleSubscriptionCatalog subscriptions, IAppleSubscriptionWriter writer,
    IAppleSignedDataVerifier verifier, IAppleAppStoreClient apple, IBillingAccessReader access, AppleBillingOptions options)
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
        var userId = await subscriptions.FindAccountAsync(transaction.AppAccountToken, cancellationToken);
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
            await writer.InvalidateAsync(userId, appAccountToken, originalTransactionId,
                exception.ObservationStartedAt, cancellationToken);
            throw;
        }
        await writer.PersistAsync(userId, appAccountToken, observation, cancellationToken);
    }

    private static AppleBillingException AccountMismatch() => new("This Apple subscription belongs to a different Solar account.", "apple_account_mismatch");
}
