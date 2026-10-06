using DeyeSolar.Domain.Models;

namespace DeyeSolar.Web.Redesign;

/// <summary>Explains a recorded policy decision without evaluating or dispatching a rule.</summary>
public static class RuleEvaluationPresentation
{
    public static IReadOnlyList<EvaluationConditionDto> Conditions(TriggerRule rule, RuleDecision decision)
    {
        var reason = decision.Reason.ToString();
        var outside = decision.Reason is RuleDecisionReason.Disabled or RuleDecisionReason.OutsideActiveWindow;
        var stopping = decision.Reason is RuleDecisionReason.SocReachedTurnOffThreshold or RuleDecisionReason.RemainOn;
        var batteryStatus = outside ? "skipped" : decision.BatterySoc is null ? "unknown"
            : stopping ? decision.BatterySoc <= decision.SocTurnOffThreshold ? "passed" : "blocked"
            : decision.BatterySoc >= decision.SocTurnOnThreshold ? "passed" : "blocked";
        var conditions = new List<EvaluationConditionDto>
        {
            new("measurement_freshness", null, null, decision.BatterySoc is null ? "unknown" : "passed", reason),
            new("battery_soc", decision.BatterySoc, stopping ? decision.SocTurnOffThreshold : decision.SocTurnOnThreshold, batteryStatus, reason)
        };
        if (decision.UsesSolarThreshold)
        {
            var skipped = outside || stopping || decision.Reason is RuleDecisionReason.MeasurementUnavailable
                or RuleDecisionReason.SocBelowTurnOnThreshold or RuleDecisionReason.Cooldown;
            conditions.Add(new("solar_average", decision.AverageSolarProductionWatts, decision.MinimumSolarProductionWatts,
                skipped ? "skipped" : decision.SolarThresholdBypassed ? "bypassed"
                : decision.AverageSolarProductionWatts is null ? "unknown"
                : decision.AverageSolarProductionWatts >= decision.MinimumSolarProductionWatts ? "passed" : "blocked", reason));
        }
        var cooldownSkipped = outside || stopping || decision.Reason is RuleDecisionReason.MeasurementUnavailable or RuleDecisionReason.SocBelowTurnOnThreshold;
        conditions.Add(new("cooldown", decision.RemainingCooldownMinutes, rule.CooldownMinutes,
            cooldownSkipped ? "skipped" : decision.RemainingCooldownMinutes > 0 ? "blocked" : "passed", reason));
        if (rule.ActiveFrom.HasValue || rule.ActiveTo.HasValue)
            conditions.Add(new("active_window", null, null, decision.Reason == RuleDecisionReason.Disabled ? "skipped"
                : decision.Reason == RuleDecisionReason.OutsideActiveWindow ? "blocked" : "passed", reason));
        return conditions;
    }
}
