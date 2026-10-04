using DeyeSolar.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Billing;

public sealed class AppleSubscriptionRefreshWorker(IServiceScopeFactory scopes,
    DbContextOptions<DeyeSolarDbContext> databaseOptions, AppleBillingOptions options,
    ILogger<AppleSubscriptionRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        using var timer = new PeriodicTimer(AppleBillingOptions.RefreshInterval);
        do
        {
            try
            {
                await using var db = new DeyeSolarDbContext(databaseOptions);
                var subscriptions = await db.Set<AppleSubscription>().AsNoTracking()
                    .Where(subscription => subscription.Environment == options.Environment)
                    .Select(subscription => new { subscription.UserId, subscription.AppAccountToken, subscription.OriginalTransactionId })
                    .ToListAsync(stoppingToken);
                foreach (var subscription in subscriptions)
                {
                    try
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        await scope.ServiceProvider.GetRequiredService<AppleBillingService>().RefreshAsync(subscription.UserId,
                            subscription.AppAccountToken, subscription.OriginalTransactionId, stoppingToken);
                    }
                    catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogWarning("Apple subscription refresh failed ({ErrorType}). Cached access expires after one hour.", exception.GetType().Name);
                    }
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Apple subscription reconciliation is unavailable ({ErrorType}).", exception.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
