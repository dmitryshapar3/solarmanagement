using DeyeSolar.Web.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DeyeSolar.Web.Operations;

public static class ApplicationHealth
{
    public static void AddApplicationHealth(this IServiceCollection services, DeploymentConfiguration deployment)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<WorkerHealthReporter>();
        services.AddSingleton<IWorkerHealthReporter>(provider => provider.GetRequiredService<WorkerHealthReporter>());
        var healthConnection = new SqlConnectionStringBuilder(deployment.ConnectionString) { ConnectTimeout = 3, ConnectRetryCount = 0 };
        var options = new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlServer(healthConnection.ConnectionString,
            sql => sql.CommandTimeout(3)).Options;
        services.AddHealthChecks()
            .AddCheck("sql-schema", new SchemaHealthCheck(options), tags: ["ready"], timeout: TimeSpan.FromSeconds(4))
            .AddCheck<WorkerHealthCheck>("background-cycles", tags: ["ready"])
            .AddCheck("durable-storage", new StorageHealthCheck([deployment.DataProtectionKeysPath, deployment.IntegrationKeysPath,
                deployment.IntegrationConfiguration["IntegrationRuntime:PackageDirectory"] ?? Path.Combine(AppContext.BaseDirectory, "data", "integration-packages")]), tags: ["ready"]);
    }

    public static void UseApplicationHealth(this WebApplication app)
    {
        app.UseWhen(context => context.Request.Path == "/health/live" || context.Request.Path == "/health/ready", branch => branch.Run(async context =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.Path == "/health/live") { await context.Response.WriteAsJsonAsync(new { status = "Healthy" }, context.RequestAborted); return; }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var report = await context.RequestServices.GetRequiredService<HealthCheckService>().CheckHealthAsync(
                    registration => registration.Tags.Contains("ready"), timeout.Token).WaitAsync(timeout.Token);
                context.Response.StatusCode = report.Status == HealthStatus.Healthy ? 200 : 503;
                await context.Response.WriteAsJsonAsync(new { status = report.Status.ToString() }, context.RequestAborted);
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            {
                context.Response.StatusCode = 503;
                await context.Response.WriteAsJsonAsync(new { status = "Unhealthy" }, context.RequestAborted);
            }
        }));
    }
}

internal sealed class SchemaHealthCheck(DbContextOptions<DeyeSolarDbContext> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try { await using var db = new DeyeSolarDbContext(options); await DatabaseSchemaVerifier.VerifyAsync(db, false, ct); return HealthCheckResult.Healthy(); }
        catch (Exception) when (!ct.IsCancellationRequested) { return HealthCheckResult.Unhealthy("Database schema is unavailable."); }
    }
}
internal sealed class WorkerHealthCheck(WorkerHealthReporter workers) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(workers.IsHealthy ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("A background cycle is unavailable."));
}
internal sealed class StorageHealthCheck(IEnumerable<string?> paths) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            try
            {
                Directory.CreateDirectory(path!);
                var probe = Path.Combine(path!, ".health-" + Guid.NewGuid().ToString("N"));
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            }
            catch (Exception) { return Task.FromResult(HealthCheckResult.Unhealthy("Durable storage is unavailable.")); }
        }
        return Task.FromResult(HealthCheckResult.Healthy());
    }
}
