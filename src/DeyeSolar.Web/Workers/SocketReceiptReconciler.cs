using DeyeSolar.Web.Data;
using Microsoft.EntityFrameworkCore;
using SolarManagement.SmartSockets.Contracts;

namespace DeyeSolar.Web.Workers;

/// <summary>Polls authoritative receipts after restarts. Never replays a command or releases an uncertain effect.</summary>
internal sealed class SocketReceiptReconciler(IDbContextFactory<DeyeSolarDbContext> factory,
    ISocketCommandTracker? tracker, ILogger logger) : ISocketReceiptReconciler
{
    public async Task ReconcileAsync(CancellationToken ct)
    {
        if (tracker is null) return;
        await using var db = await factory.CreateDbContextAsync(ct);
        var pending = await db.IntegrationCommands.AsNoTracking()
            .Where(command => command.Status == "requested" || command.Status == "pending" || command.Status == "uncertain")
            .Select(command => new { command.DeviceId, command.Id }).ToListAsync(ct);
        await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (command, token) =>
            {
                try { await tracker.ReadResultAsync(new(command.DeviceId), new(command.Id), token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) { logger.LogWarning("Command receipt reconciliation unavailable ({ErrorType})", error.GetType().Name); }
            });
    }
}
