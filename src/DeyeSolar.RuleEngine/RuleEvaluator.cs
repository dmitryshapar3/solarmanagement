using DeyeSolar.Domain.Models;

namespace DeyeSolar.RuleEngine;

public class RuleEvaluator
{
    public IReadOnlyList<RuleAction> Evaluate(
        InverterData current,
        IReadOnlyList<InverterData> recentReadings,
        IEnumerable<TriggerRule> rules,
        DateTimeOffset now)
    {
        var actions = new List<RuleAction>();

        foreach (var rule in rules.Where(r => r.Enabled))
        {
            if (!IsInTimeWindow(rule, now))
                continue;

            bool onConditionsMet = CheckOnConditions(current, recentReadings, rule, now);
            bool forceOff = CheckForceOff(recentReadings, rule, now);

            if (!rule.CurrentState && onConditionsMet && !forceOff)
            {
                // Currently OFF, conditions say turn ON
                actions.Add(new RuleAction(rule.Id, rule.EntityId, TurnOn: true));
            }
            else if (rule.CurrentState && (!onConditionsMet || forceOff))
            {
                // Currently ON, but conditions no longer met OR force-off triggered
                actions.Add(new RuleAction(rule.Id, rule.EntityId, TurnOn: false));
            }
        }

        return actions;
    }

    private static bool CheckOnConditions(
        InverterData current,
        IReadOnlyList<InverterData> readings,
        TriggerRule rule,
        DateTimeOffset now)
    {
        bool hasAnyCondition = false;

        if (rule.SocTurnOnThreshold.HasValue)
        {
            hasAnyCondition = true;
            if (current.BatterySoc < rule.SocTurnOnThreshold.Value)
                return false;
        }

        if (rule.MinSolarPowerWatts.HasValue && rule.SolarSustainedMinutes.HasValue)
        {
            hasAnyCondition = true;
            if (!IsSolarSustained(readings, rule.MinSolarPowerWatts.Value,
                    rule.SolarSustainedMinutes.Value, now))
                return false;
        }

        return hasAnyCondition;
    }

    private static bool CheckForceOff(
        IReadOnlyList<InverterData> readings,
        TriggerRule rule,
        DateTimeOffset now)
    {
        if (rule.DischargeSustainedMinutes.HasValue)
            return IsDischarging(readings, rule.DischargeSustainedMinutes.Value, now);

        return false;
    }

    private static bool IsSolarSustained(
        IReadOnlyList<InverterData> readings,
        int minWatts,
        int minutes,
        DateTimeOffset now)
    {
        var cutoff = now.AddMinutes(-minutes);
        var relevant = readings.Where(r => r.Timestamp >= cutoff).ToList();

        if (relevant.Count < 3)
            return false;

        var earliest = relevant.Min(r => r.Timestamp);
        if ((now - earliest).TotalMinutes < minutes - 1)
            return false;

        return relevant.All(r => r.SolarProduction >= minWatts);
    }

    private static bool IsDischarging(
        IReadOnlyList<InverterData> readings,
        int minutes,
        DateTimeOffset now)
    {
        var cutoff = now.AddMinutes(-minutes);
        var relevant = readings.Where(r => r.Timestamp >= cutoff).ToList();

        if (relevant.Count < 3)
            return false;

        var earliest = relevant.Min(r => r.Timestamp);
        if ((now - earliest).TotalMinutes < minutes - 1)
            return false;

        return relevant.All(r => r.BatteryPower > 0);
    }

    private static bool IsInTimeWindow(TriggerRule rule, DateTimeOffset now)
    {
        if (!rule.ActiveFrom.HasValue || !rule.ActiveTo.HasValue)
            return true;

        var currentTime = TimeOnly.FromDateTime(now.LocalDateTime);

        if (rule.ActiveFrom.Value <= rule.ActiveTo.Value)
            return currentTime >= rule.ActiveFrom.Value && currentTime <= rule.ActiveTo.Value;

        return currentTime >= rule.ActiveFrom.Value || currentTime <= rule.ActiveTo.Value;
    }
}
