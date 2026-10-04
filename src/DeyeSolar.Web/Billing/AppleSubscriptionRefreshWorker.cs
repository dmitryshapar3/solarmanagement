using DeyeSolar.Web.Operations;

namespace DeyeSolar.Web.Billing;

public sealed class AppleSubscriptionRefreshWorker(IServiceScopeFactory scopes,
    AppleBillingOptions options,
    ILogger<AppleSubscriptionRefreshWorker> logger, IWorkerHealthReporter health) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        health.Started("apple-subscriptions", AppleBillingOptions.RefreshInterval + TimeSpan.FromMinutes(5));
        using var timer = new PeriodicTimer(AppleBillingOptions.RefreshInterval);
        do
        {
            try
            {
                const int pageSize = 100;
                for (var offset = 0; ; offset += pageSize)
                {
                    await using var batch = scopes.CreateAsyncScope();
                    var subscriptions = await batch.ServiceProvider.GetRequiredService<IAppleSubscriptionCatalog>()
                        .ReadPageAsync(offset, pageSize, stoppingToken);
                    health.Succeeded("apple-subscriptions");
                    foreach (var subscription in subscriptions)
                    {
                        try
                        {
                            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                            deadline.CancelAfter(TimeSpan.FromMinutes(2));
                            await using var scope = scopes.CreateAsyncScope();
                            await scope.ServiceProvider.GetRequiredService<AppleBillingService>().RefreshAsync(subscription.UserId,
                                subscription.AppAccountToken, subscription.OriginalTransactionId, deadline.Token);
                        }
                        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                        {
                            logger.LogWarning("Apple subscription refresh failed ({ErrorType}). Cached access expires after one hour.", exception.GetType().Name);
                        }
                        health.Succeeded("apple-subscriptions");
                    }
                    if (subscriptions.Count < pageSize) break;
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                health.Failed("apple-subscriptions");
                logger.LogWarning("Apple subscription reconciliation is unavailable ({ErrorType}).", exception.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
