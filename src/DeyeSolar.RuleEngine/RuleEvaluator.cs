using DeyeSolar.Domain.Models;

namespace DeyeSolar.RuleEngine;

public class RuleEvaluator
{
    public IReadOnlyList<RuleAction> Evaluate(InverterData data, IEnumerable<TriggerRule> rules, DateTimeOffset now)
    {
        var actions = new List<RuleAction>();

        foreach (var rule in rules.Where(r => r.Enabled))
        {
            if (!IsInTimeWindow(rule, now))
                continue;

            if (IsInCooldown(rule, now))
                continue;

            bool shouldBeOn = EvaluateConditions(data, rule);

            if (shouldBeOn && !rule.CurrentState)
                actions.Add(new RuleAction(rule.Id, rule.EntityId, TurnOn: true));
            else if (!shouldBeOn && rule.CurrentState)
                actions.Add(new RuleAction(rule.Id, rule.EntityId, TurnOn: false));
        }

        return actions;
    }

    private static bool EvaluateConditions(InverterData data, TriggerRule rule)
    {
        bool hasSocCondition = rule.SocTurnOnThreshold.HasValue && rule.SocTurnOffThreshold.HasValue;
        bool hasSurplusCondition = rule.MinSolarSurplusWatts.HasValue;

        // If no conditions configured, rule doesn't trigger
        if (!hasSocCondition && !hasSurplusCondition)
            return false;

        // All configured conditions must be met (AND logic)
        if (hasSocCondition)
        {
            bool socMet = rule.CurrentState
                ? data.BatterySoc > rule.SocTurnOffThreshold!.Value  // stay on above off-threshold
                : data.BatterySoc >= rule.SocTurnOnThreshold!.Value; // turn on at on-threshold

            if (!socMet)
                return false;
        }

        if (hasSurplusCondition)
        {
            int surplus = data.SolarProduction - data.LoadPower;
            if (surplus < rule.MinSolarSurplusWatts!.Value)
                return false;
        }

        return true;
    }

    private static bool IsInTimeWindow(TriggerRule rule, DateTimeOffset now)
    {
        if (!rule.ActiveFrom.HasValue || !rule.ActiveTo.HasValue)
            return true;

        var currentTime = TimeOnly.FromDateTime(now.LocalDateTime);

        if (rule.ActiveFrom.Value <= rule.ActiveTo.Value)
            return currentTime >= rule.ActiveFrom.Value && currentTime <= rule.ActiveTo.Value;

        // Spans midnight (e.g., 22:00 to 06:00)
        return currentTime >= rule.ActiveFrom.Value || currentTime <= rule.ActiveTo.Value;
    }

    private static bool IsInCooldown(TriggerRule rule, DateTimeOffset now)
    {
        if (!rule.LastTriggered.HasValue)
            return false;

        return (now - rule.LastTriggered.Value).TotalSeconds < rule.CooldownSeconds;
    }
}
