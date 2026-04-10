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

            if (rule.CurrentState)
            {
                if (ShouldTurnOff(current, recentReadings, rule, now))
                    actions.Add(new RuleAction(rule.Id, rule.EntityId, TurnOn: false));
            }
            else
            {
                if (ShouldTurnOn(current, rule, now))
                    actions.Add(new RuleAction(rule.Id, rule.EntityId, TurnOn: true));
            }
        }

        return actions;
    }

    private static bool ShouldTurnOn(InverterData current, TriggerRule rule, DateTimeOffset now)
    {
        // SOC must be at or above turn-on threshold
        if (current.BatterySoc < rule.SocTurnOnThreshold)
            return false;

        // Never turn on at or below the safety floor
        if (current.BatterySoc <= rule.SocFloor)
            return false;

        // Optional: require the battery to be actively charging (surplus solar)
        // Sign convention: BatteryPower > 0 = discharging, < 0 = charging
        if (rule.RequireBatteryCharging && current.BatteryPower >= 0)
            return false;

        // Cooldown: respect time since last turn-off
        if (rule.CurrentStateChangedAt.HasValue)
        {
            var elapsed = (now - new DateTimeOffset(rule.CurrentStateChangedAt.Value, TimeSpan.Zero)).TotalMinutes;
            if (elapsed < rule.CooldownMinutes)
                return false;
        }

        return true;
    }

    private static bool ShouldTurnOff(
        InverterData current,
        IReadOnlyList<InverterData> readings,
        TriggerRule rule,
        DateTimeOffset now)
    {
        // Safety floor overrides everything, including MinOnMinutes
        if (current.BatterySoc <= rule.SocFloor)
            return true;

        // Honor minimum on-time to prevent flapping on short cloud transients
        if (rule.CurrentStateChangedAt.HasValue)
        {
            var elapsed = (now - new DateTimeOffset(rule.CurrentStateChangedAt.Value, TimeSpan.Zero)).TotalMinutes;
            if (elapsed < rule.MinOnMinutes)
                return false;
        }

        var drainWh = CalculateNetBatteryDrainWh(readings, rule.DrainWindowMinutes, now);
        return drainWh >= rule.MaxDrainWh;
    }

    // Signed trapezoidal integral of BatteryPower over the window.
    // Positive BatteryPower = discharging (drain), negative = charging (cancels prior drain).
    // Grid import/export is deliberately ignored — only the battery matters for "don't drain the battery".
    public static double CalculateNetBatteryDrainWh(
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

            var avgBatteryPower = (prev.BatteryPower + curr.BatteryPower) / 2.0;
            totalWh += avgBatteryPower * intervalHours;
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
