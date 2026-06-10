using DeyeSolar.Domain.Models;

namespace DeyeSolar.RuleEngine;

public class RuleEvaluator
{
    public const int SolarProductionBypassSocThreshold = 95;
    public const int SolarProductionAverageWindowMinutes = 60;

    public IReadOnlyList<RuleAction> Evaluate(
        InverterData current,
        IEnumerable<TriggerRule> rules,
        DateTimeOffset now,
        string? timeZoneId = null,
        RuleEvaluationContext? context = null)
    {
        var actions = new List<RuleAction>();

        foreach (var rule in rules.Where(r => r.Enabled))
        {
            if (!IsInTimeWindow(rule, now, timeZoneId))
            {
                if (rule.CurrentState)
                    actions.Add(new RuleAction(rule.Id, rule.EntityId, TurnOn: false));
                continue;
            }

            if (rule.CurrentState)
            {
                if (ShouldTurnOff(current, rule))
                    actions.Add(new RuleAction(rule.Id, rule.EntityId, TurnOn: false));
            }
            else
            {
                if (ShouldTurnOn(current, rule, now, context))
                    actions.Add(new RuleAction(rule.Id, rule.EntityId, TurnOn: true));
            }
        }

        return actions;
    }

    private static bool ShouldTurnOn(
        InverterData current,
        TriggerRule rule,
        DateTimeOffset now,
        RuleEvaluationContext? context)
    {
        // SOC must be at or above turn-on threshold
        if (current.BatterySoc < rule.SocTurnOnThreshold)
            return false;

        // Cooldown: respect time since last turn-off
        if (rule.CurrentStateChangedAt.HasValue)
        {
            var elapsed = (now - new DateTimeOffset(rule.CurrentStateChangedAt.Value, TimeSpan.Zero)).TotalMinutes;
            if (elapsed < rule.CooldownMinutes)
                return false;
        }

        return IsSolarProductionConditionSatisfied(current, rule, context);
    }

    public static bool IsSolarProductionConditionSatisfied(
        InverterData current,
        TriggerRule rule,
        RuleEvaluationContext? context)
    {
        if (!rule.UseSolarProductionThreshold)
            return true;

        if (current.BatterySoc >= SolarProductionBypassSocThreshold)
            return true;

        var averageWatts = context?.AverageSolarProductionWatts;
        return averageWatts.HasValue && averageWatts.Value >= rule.MinAverageSolarProductionWatts;
    }

    private static bool ShouldTurnOff(InverterData current, TriggerRule rule)
    {
        var threshold = GetSocTurnOffThreshold(rule);
        return current.BatterySoc <= threshold;
    }

    private static int GetSocTurnOffThreshold(TriggerRule rule)
        => rule.UseSeparateSocTurnOffThreshold ? rule.SocTurnOffThreshold : rule.SocTurnOnThreshold;

    private static bool IsInTimeWindow(TriggerRule rule, DateTimeOffset now, string? timeZoneId)
    {
        if (!rule.ActiveFrom.HasValue || !rule.ActiveTo.HasValue)
            return true;

        DateTime localNow;
        if (!string.IsNullOrEmpty(timeZoneId))
        {
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
                localNow = TimeZoneInfo.ConvertTime(now, tz).DateTime;
            }
            catch
            {
                localNow = now.UtcDateTime;
            }
        }
        else
        {
            localNow = now.UtcDateTime;
        }

        var currentTime = TimeOnly.FromDateTime(localNow);

        if (rule.ActiveFrom.Value <= rule.ActiveTo.Value)
            return currentTime >= rule.ActiveFrom.Value && currentTime <= rule.ActiveTo.Value;

        return currentTime >= rule.ActiveFrom.Value || currentTime <= rule.ActiveTo.Value;
    }
}
