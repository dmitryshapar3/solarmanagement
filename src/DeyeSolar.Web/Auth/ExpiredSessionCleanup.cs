using DeyeSolar.Web.Data;
using DeyeSolar.Web.Operations;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Auth;

public interface IExpiredSecurityRecordsCleaner
{
    Task<int> DeleteBatchAsync(CancellationToken ct);
}

public sealed class SqlExpiredSessionCleaner(DbContextOptions<DeyeSolarDbContext> database, TimeProvider clock) : IExpiredSecurityRecordsCleaner
{
    public async Task<int> DeleteBatchAsync(CancellationToken ct)
    {
        await using var db = new DeyeSolarDbContext(database);
        var now = clock.GetUtcNow().UtcDateTime;
        return await db.AccountSessions.Where(session => session.ExpiresAt <= now)
            .OrderBy(session => session.ExpiresAt).Take(500).ExecuteDeleteAsync(ct);
    }
}

public sealed class SqlExpiredIntegrationAuthorizationCleaner(DbContextOptions<DeyeSolarDbContext> database, TimeProvider clock)
    : IExpiredSecurityRecordsCleaner
{
    public async Task<int> DeleteBatchAsync(CancellationToken ct)
    {
        await using var db = new DeyeSolarDbContext(database);
        // Keep an expired status briefly for client feedback; expired proofs cannot authorize any operation.
        var cutoff = clock.GetUtcNow().AddDays(-1);
        return await db.IntegrationOAuthFlows.IgnoreQueryFilters().Where(flow => flow.ExpiresAt < cutoff)
            .OrderBy(flow => flow.ExpiresAt).Take(500).ExecuteDeleteAsync(ct);
    }
}

public sealed class ExpiredSecurityCleanupWorker(IEnumerable<IExpiredSecurityRecordsCleaner> cleaners, IWorkerHealthReporter health,
    ILogger<ExpiredSecurityCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        const string name = "expired-security-records";
        health.Started(name, TimeSpan.FromHours(2));
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = TimeSpan.FromHours(1);
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(30));
                    foreach (var cleaner in cleaners)
                        while (await cleaner.DeleteBatchAsync(deadline.Token) == 500) { }
                    health.Succeeded(name);
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    health.Failed(name);
                    logger.LogWarning("Expired security record cleanup is unavailable ({ErrorType}).", error.GetType().Name);
                    delay = TimeSpan.FromMinutes(1);
                }
                await Task.Delay(delay, stoppingToken);
            }
        }
        finally { if (stoppingToken.IsCancellationRequested) health.Stopped(name); }
    }
}

public static class ExpiredSessionCleanupRegistration
{
    public static IServiceCollection AddExpiredSessionCleanup(this IServiceCollection services)
    {
        services.AddSingleton<IExpiredSecurityRecordsCleaner, SqlExpiredSessionCleaner>();
        services.AddSingleton<IExpiredSecurityRecordsCleaner, SqlExpiredIntegrationAuthorizationCleaner>();
        services.AddHostedService<ExpiredSecurityCleanupWorker>();
        return services;
    }
}
