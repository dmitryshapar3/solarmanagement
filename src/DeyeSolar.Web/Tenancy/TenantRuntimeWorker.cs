namespace DeyeSolar.Web.Tenancy;

/// <summary>Schedules independent tenant cycles with bounded worker concurrency; never uses the legacy global worker.</summary>
public sealed class TenantRuntimeWorker(TenantRuntimeRegistry registry, ILogger<TenantRuntimeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var ids = await registry.EnabledInstallationIdsAsync(stoppingToken).ConfigureAwait(false);
                await registry.RemoveDisabledAsync(ids.ToHashSet(StringComparer.Ordinal)).ConfigureAwait(false);
                await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = stoppingToken },
                    async (id, ct) =>
                    {
                        try { await (await registry.GetAsync(id, ct).ConfigureAwait(false)).RunDueWorkAsync(ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                        catch (Exception exception) { logger.LogWarning("An installation cycle is unavailable ({ErrorType})", exception.GetType().Name); }
                    }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning("Installation scheduling is unavailable ({ErrorType})", exception.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
