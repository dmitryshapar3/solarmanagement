using DeyeSolar.Domain.Models;

namespace DeyeSolar.RuleEngine;

public class RuleEvaluator : IRuleDecisionEvaluator
{
    public const int SolarProductionBypassSocThreshold = 95;
    public const int SolarProductionAverageWindowMinutes = 60;

    public IReadOnlyList<RuleAction> Evaluate(
        InverterData? current,
        IEnumerable<TriggerRule> rules,
        DateTimeOffset now,
        string? timeZoneId = null,
        RuleEvaluationContext? context = null)
    {
        return rules.Select(rule => Decide(current, rule, now, timeZoneId, context).Action)
            .OfType<RuleAction>().ToArray();
    }

    public RuleDecision Decide(InverterData? current, TriggerRule rule, DateTimeOffset now,
        string? timeZoneId = null, RuleEvaluationContext? context = null)
    {
        var turnOffThreshold = rule.UseSeparateSocTurnOffThreshold ? rule.SocTurnOffThreshold : rule.SocTurnOnThreshold;
        var remainingCooldown = 0d;
        var batterySoc = HasFreshSoc(current, now) ? current.BatterySoc : (int?)null;
        RuleDecision Result(RuleDecisionReason reason, bool? turnOn = null) => new(rule.Id, rule.EntityId, now, turnOn, reason,
            batterySoc, rule.SocTurnOnThreshold, turnOffThreshold, remainingCooldown,
            rule.UseSolarProductionThreshold, batterySoc >= SolarProductionBypassSocThreshold,
            context?.AverageSolarProductionWatts, rule.MinAverageSolarProductionWatts);
        if (!rule.Enabled) return Result(RuleDecisionReason.Disabled);
        if (!IsInTimeWindow(rule, now, timeZoneId))
            return Result(RuleDecisionReason.OutsideActiveWindow, rule.CurrentState ? false : null);
        if (batterySoc is not { } soc) return Result(RuleDecisionReason.MeasurementUnavailable);
        if (rule.CurrentState)
            return soc <= turnOffThreshold ? Result(RuleDecisionReason.SocReachedTurnOffThreshold, false)
                : Result(RuleDecisionReason.RemainOn);
        if (soc < rule.SocTurnOnThreshold) return Result(RuleDecisionReason.SocBelowTurnOnThreshold);
        if (rule.CurrentStateChangedAt is { } changed)
            remainingCooldown = Math.Max(0, rule.CooldownMinutes - (now - new DateTimeOffset(changed, TimeSpan.Zero)).TotalMinutes);
        if (remainingCooldown > 0) return Result(RuleDecisionReason.Cooldown);
        if (!IsSolarProductionConditionSatisfied(soc, rule, context))
            return Result(context?.AverageSolarProductionWatts is null
                ? RuleDecisionReason.SolarAverageUnavailable : RuleDecisionReason.SolarAverageBelowThreshold);
        return Result(RuleDecisionReason.TurnOnConditionsSatisfied, true);
    }

    public static bool HasFreshSoc([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] InverterData? current, DateTimeOffset now)
    {
        if (current is null || !current.BatterySocValid || current.Telemetry is null) return false;
        var soc = current.Telemetry.BatterySoc;
        return soc.Quality == SolarManagement.Inverters.Contracts.MeasurementQuality.Good
            && soc.ObservedAt is { } observed && observed <= now && now - observed <= TimeSpan.FromMinutes(10);
    }

    public static bool IsSolarProductionConditionSatisfied(
        InverterData current,
        TriggerRule rule,
        RuleEvaluationContext? context)
        => IsSolarProductionConditionSatisfied(current.BatterySoc, rule, context);

    private static bool IsSolarProductionConditionSatisfied(int batterySoc, TriggerRule rule, RuleEvaluationContext? context)
    {
        if (!rule.UseSolarProductionThreshold)
            return true;

        if (batterySoc >= SolarProductionBypassSocThreshold)
            return true;

        var averageWatts = context?.AverageSolarProductionWatts;
        return averageWatts.HasValue && averageWatts.Value >= rule.MinAverageSolarProductionWatts;
    }

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
