using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Integrations;
using Microsoft.EntityFrameworkCore;
using SolarManagement.SmartSockets.Contracts;

namespace DeyeSolar.Web.Workers;

/// <summary>Reconciles physical observations without allowing a stale read to overwrite a newer command or edit.</summary>
internal sealed class RuleObservationReconciler(IDbContextFactory<DeyeSolarDbContext> factory, ISocketController sockets) : IRuleObservationReconciler
{
    public async Task<bool> ReconcileAsync(TriggerRule expected, DateTime now, CancellationToken ct)
    {
        bool observed;
        try
        {
            if (sockets is ISocketCommandTracker tracker && Guid.TryParse(expected.EntityId, out var deviceId)
                && (await tracker.ListUnresolvedAsync(new(deviceId), ct)).Count > 0) return false;
            observed = await sockets.GetStateAsync(expected.EntityId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (DeyeSolar.Web.Billing.BillingAccessException) { throw; }
        catch { return false; }
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var query = db.TriggerRules.AsQueryable();
        if (db.Database.IsSqlServer())
            query = db.TriggerRules.FromSqlInterpolated($"SELECT * FROM [TriggerRules] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {expected.Id}");
        var current = await query.SingleOrDefaultAsync(rule => rule.Id == expected.Id, ct);
        if (current is null || !IntegrationAutomationSourceGuard.SameConfiguration(expected, current)
            || current.CurrentState != expected.CurrentState || current.CurrentStateChangedAt != expected.CurrentStateChangedAt)
            return false;
        if (current.CurrentState != observed)
        {
            current.CurrentState = observed;
            current.CurrentStateChangedAt = now;
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        expected.CurrentState = current.CurrentState;
        expected.CurrentStateChangedAt = current.CurrentStateChangedAt;
        return true;
    }
}
