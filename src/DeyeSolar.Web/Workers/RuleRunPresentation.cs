using DeyeSolar.Domain.Models;
using DeyeSolar.RuleEngine;

namespace DeyeSolar.Web.Workers;

/// <summary>Formats an already evaluated outcome; it never decides whether a device should switch.</summary>
internal sealed record RuleRunPresentation(string Action, string ConditionKey, string Reason)
{
    public static RuleRunPresentation From(RuleRunOutcome outcome)
    {
        if (outcome.Failure is { } failure) return new("ERROR", $"error:{failure}", failure);
        var decision = outcome.Decision ?? throw new InvalidOperationException("A recorded rule requires its policy decision.");
        if (outcome.ActionSucceeded)
        {
            if (decision.TurnOn == true)
            {
                var solar = DescribeSolar(decision);
                var reason = $"SOC={decision.BatterySoc}% >= {decision.SocTurnOnThreshold}%, cooldown elapsed";
                return new("ON", "action:on", string.IsNullOrEmpty(solar) ? reason : reason + "; " + solar);
            }
            return decision.Reason == RuleDecisionReason.SocReachedTurnOffThreshold
                ? new("OFF", "action:off:soc-threshold", $"SOC={decision.BatterySoc}% <= turn-off threshold {decision.SocTurnOffThreshold}%")
                : new("OFF", "action:off:time-window", "Outside active time window");
        }
        return decision.Reason switch
        {
            RuleDecisionReason.RemainOn => new("NO_CHANGE", "no-change:on", $"ON: SOC={decision.BatterySoc}% (turn off <= {decision.SocTurnOffThreshold}%)"),
            RuleDecisionReason.SocBelowTurnOnThreshold => new("NO_CHANGE", "no-change:off:soc-below-threshold", $"OFF: SOC={decision.BatterySoc}% (need >= {decision.SocTurnOnThreshold}%)"),
            RuleDecisionReason.Cooldown => new("NO_CHANGE", "no-change:off:cooldown", $"OFF: cooldown active ({Math.Ceiling(decision.RemainingCooldownMinutes)} min remaining)"),
            RuleDecisionReason.SolarAverageUnavailable => new("NO_CHANGE", "no-change:off:solar-average-unavailable", "OFF: " + DescribeSolar(decision)),
            RuleDecisionReason.SolarAverageBelowThreshold => new("NO_CHANGE", "no-change:off:solar-average-below-threshold", "OFF: " + DescribeSolar(decision)),
            RuleDecisionReason.OutsideActiveWindow => new("NO_CHANGE", "no-change:off:time-window", "OFF: outside active time window"),
            RuleDecisionReason.MeasurementUnavailable => new("NO_CHANGE", "no-change:measurement-unavailable", "Fresh battery SOC is unavailable"),
            _ => new("NO_CHANGE", "no-change:off:conditions-not-met", "OFF: conditions not met")
        };
    }

    private static string DescribeSolar(RuleDecision decision)
    {
        if (!decision.UsesSolarThreshold) return "";
        if (decision.SolarThresholdBypassed) return $"PV avg threshold bypassed at SOC >= {RuleEvaluator.SolarProductionBypassSocThreshold}%";
        if (decision.AverageSolarProductionWatts is not { } average)
            return $"PV avg last {RuleEvaluator.SolarProductionAverageWindowMinutes}m unavailable while SOC < {RuleEvaluator.SolarProductionBypassSocThreshold}%";
        return $"PV avg last {RuleEvaluator.SolarProductionAverageWindowMinutes}m={average}W (need >= {decision.MinimumSolarProductionWatts}W while SOC < {RuleEvaluator.SolarProductionBypassSocThreshold}%)";
    }
}
