using DeyeSolar.Domain.Models;
using DeyeSolar.RuleEngine;
using SolarManagement.Inverters.Contracts;

namespace DeyeSolar.Web.Workers;

internal interface IRuleRunHistory
{
    Task CleanupAsync(CancellationToken ct);
    Task<RuleEvaluationContext> BuildContextAsync(DateTime now, string deviceKey, CancellationToken ct);
    Task RecordAsync(InverterData data, RuleEvaluationContext context, List<TriggerRule> rules,
        IReadOnlyList<RuleAction> actions, IReadOnlySet<int> successfulActions,
        IReadOnlyDictionary<int, string> failedActions, CancellationToken ct);
}

internal interface IRuleObservationReconciler
{
    Task<bool> ReconcileAsync(TriggerRule rule, DateTime now, CancellationToken ct);
}

internal interface IRuleAutomationExecutor
{
    Task EvaluateAsync(InverterData? data, List<TriggerRule> rules, Guid? effectiveSource, DateTime now,
        Func<Task<bool>> isCurrent, string deviceKey, CancellationToken ct);
}

internal interface ISocketReceiptReconciler
{
    Task ReconcileAsync(CancellationToken ct);
}
