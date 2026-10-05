using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Integrations;

// Carries the source used for an automatic decision to the durable command transaction.
// Manual commands do not enter a scope. The existing instance lock also protects link edits.
internal static class IntegrationAutomationSourceGuard
{
    private sealed record Decision(Guid? Source, TriggerRule? Rule, InverterData? Observation, bool? TurnOn, Func<bool>? Revalidate);
    private static readonly AsyncLocal<Decision?> Current = new();

    public static IDisposable Enter(Guid? source, TriggerRule? rule = null, InverterData? observation = null, bool? turnOn = null,
        Func<bool>? revalidate = null)
    {
        var previous = Current.Value;
        Current.Value = new(source, rule, observation, turnOn, revalidate);
        return new Scope(previous);
    }

    public static async Task ValidateAsync(DeyeSolarDbContext db, Guid deviceId, CancellationToken ct)
    {
        if (Current.Value is not { } decision) return;
        ValidateDecision();
        if (decision.Observation is { InverterId: { } sourceId } observation)
        {
            if (!await IntegrationPersistenceGuard.LockCurrentAsync(db, sourceId, observation.ConfigurationRevision, observation.RuntimeGeneration, ct))
                throw new InvalidOperationException("The source inverter connection changed after the rule was evaluated.");
            var source = await db.IntegrationDeviceBindings.AsNoTracking().SingleAsync(b => b.Id == sourceId, ct);
            if (source.Kind != "inverter" || decision.Source is null && decision.Rule?.SourceInverterId is null && !source.IsDefault)
                throw new InvalidOperationException("The selected source inverter changed after the rule was evaluated.");
        }
        if (decision.Rule?.SourceInverterId is null)
        {
            var binding = await db.IntegrationDeviceBindings.AsNoTracking().SingleAsync(b => b.Id == deviceId, ct);
            if (IntegrationSocketAssociation.Read(binding).SourceInverterId != decision.Source)
                throw new InvalidOperationException("The socket's source inverter changed after the rule was evaluated.");
        }
        if (decision.Rule is { } expected)
        {
            var query = db.TriggerRules.AsNoTracking();
            if (db.Database.IsSqlServer())
                query = db.TriggerRules.FromSqlInterpolated($"SELECT * FROM [TriggerRules] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {expected.Id}").AsNoTracking();
            var rule = await query.SingleOrDefaultAsync(r => r.Id == expected.Id, ct);
            if (rule is null || !SameConfiguration(expected, rule))
                throw new InvalidOperationException("The rule changed after it was evaluated.");
        }
        ValidateDecision();
    }

    public static void ValidateDecision()
    {
        if (Current.Value is not { } decision) return;
        if (decision.TurnOn == true && !DeyeSolar.RuleEngine.RuleEvaluator.HasFreshSoc(decision.Observation, DateTimeOffset.UtcNow)
            || decision.Revalidate?.Invoke() == false)
            throw new AutomationDecisionExpiredException();
    }

    public static bool SameConfiguration(TriggerRule expected, TriggerRule current) =>
        RuleConfigurationSnapshot.From(expected).Execution == RuleConfigurationSnapshot.From(current).Execution;

    private sealed class Scope(Decision? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

internal sealed class AutomationDecisionExpiredException()
    : InvalidOperationException("The automatic decision expired before the socket command was dispatched.");
