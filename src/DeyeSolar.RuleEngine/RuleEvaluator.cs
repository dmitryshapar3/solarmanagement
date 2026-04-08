using DeyeSolar.Domain.Models;

namespace DeyeSolar.RuleEngine;

public class RuleEvaluator
{
    public IReadOnlyList<RuleAction> Evaluate(
        InverterData current,
        IReadOnlyList<InverterData> recentReadings,
        IEnumerable<TriggerRule> rules,
        DateTimeOffset now,
        string? timeZoneId = null)
    {
        var actions = new List<RuleAction>();

        foreach (var rule in rules.Where(r => r.Enabled))
        {
            if (!IsInTimeWindow(rule, now, timeZoneId))
                continue;

            if (!rule.CurrentState)
            {
                if (ShouldTurnOn(current, rule, now))
                    actions.Add(new RuleAction(rule.Id, rule.EntityId, TurnOn: true));
            }
            else
            {
                if (ShouldTurnOff(recentReadings, rule, now))
                    actions.Add(new RuleAction(rule.Id, rule.EntityId, TurnOn: false));
            }
        }

        return actions;
    }

    private static bool ShouldTurnOn(InverterData current, TriggerRule rule, DateTimeOffset now)
    {
        // Must have SOC threshold configured
        if (!rule.SocTurnOnThreshold.HasValue)
            return false;

        // SOC must be at or above threshold
        if (current.BatterySoc < rule.SocTurnOnThreshold.Value)
            return false;

        // Cooldown: don't turn on if recently turned off
        if (rule.LastTurnedOff.HasValue)
        {
            var elapsed = (now - new DateTimeOffset(rule.LastTurnedOff.Value, TimeSpan.Zero)).TotalMinutes;
            if (elapsed < rule.CooldownMinutes)
                return false;
        }

        return true;
    }

    private static bool ShouldTurnOff(IReadOnlyList<InverterData> readings, TriggerRule rule, DateTimeOffset now)
    {
        if (!rule.MaxConsumptionWh.HasValue)
            return false;

        var consumptionWh = CalculateAccumulatedConsumption(readings, rule.MonitoringWindowMinutes, now);
        return consumptionWh >= rule.MaxConsumptionWh.Value;
    }

    public static double CalculateAccumulatedConsumption(
        IReadOnlyList<InverterData> readings,
        int windowMinutes,
        DateTimeOffset now)
    {
        var cutoff = now.AddMinutes(-windowMinutes);
        var relevant = readings
            .Where(r => r.Timestamp >= cutoff)
            .OrderBy(r => r.Timestamp)
            .ToList();

        if (relevant.Count < 2)
            return 0;

        double totalWh = 0;

        for (int i = 1; i < relevant.Count; i++)
        {
            var prev = relevant[i - 1];
            var curr = relevant[i];
            var intervalHours = (curr.Timestamp - prev.Timestamp).TotalHours;

            if (intervalHours <= 0 || intervalHours > 0.5) // skip gaps > 30 min
                continue;

            // Average power drawn from battery + grid between two readings
            // BatteryPower > 0 = discharging, GridConsumption > 0 = importing
            var avgBatteryDraw = (Math.Max(0, prev.BatteryPower) + Math.Max(0, curr.BatteryPower)) / 2.0;
            var avgGridDraw = (Math.Max(0, prev.GridConsumption) + Math.Max(0, curr.GridConsumption)) / 2.0;

            totalWh += (avgBatteryDraw + avgGridDraw) * intervalHours;
        }

        return totalWh;
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
