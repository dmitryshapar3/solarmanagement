using SolarManagement.Inverters.Contracts;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using DeyeSolar.RuleEngine;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Services;
using DeyeSolar.Web.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Web.Workers;

/// <summary>Reads evaluation history and records rule outcomes; it never controls devices.</summary>
internal sealed class RuleRunHistory(IDbContextFactory<DeyeSolarDbContext> _dbFactory, ILogger _logger) : IRuleRunHistory
{
    private const int HistoryRetentionDays = 31;
    public async Task RecordAsync(
        InverterData data,
        RuleEvaluationContext evaluationContext,
        List<TriggerRule> rules,
        IReadOnlyList<RuleAction> actions,
        IReadOnlySet<int> successfulActions,
        IReadOnlyDictionary<int, string> failedActions,
        CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var actionsByRule = actions.ToDictionary(a => a.RuleId);

            foreach (var rule in rules.Where(r => r.Enabled))
            {
                string action;
                string reason;
                string conditionKey;

                if (actionsByRule.TryGetValue(rule.Id, out var ruleAction) &&
                    successfulActions.Contains(rule.Id))
                {
                    action = ruleAction.TurnOn ? "ON" : "OFF";
                    var turnOffThreshold = GetSocTurnOffThreshold(rule);
                    if (ruleAction.TurnOn)
                    {
                        conditionKey = "action:on";
                        reason = $"SOC={data.BatterySoc}% >= {rule.SocTurnOnThreshold}%, cooldown elapsed";
                        var solarReason = DescribeSolarProductionCondition(data, rule, evaluationContext);
                        if (!string.IsNullOrEmpty(solarReason))
                            reason += $"; {solarReason}";
                    }
                    else if (data.BatterySocValid != false && data.BatterySoc <= turnOffThreshold)
                    {
                        conditionKey = "action:off:soc-threshold";
                        reason = $"SOC={data.BatterySoc}% <= turn-off threshold {turnOffThreshold}%";
                    }
                    else
                    {
                        conditionKey = "action:off:time-window";
                        reason = "Outside active time window";
                    }
                }
                else if (failedActions.TryGetValue(rule.Id, out var failureReason))
                {
                    action = "ERROR";
                    reason = failureReason;
                    conditionKey = $"error:{failureReason}";
                }
                else
                {
                    action = "NO_CHANGE";
                    if (rule.CurrentState)
                    {
                        conditionKey = "no-change:on";
                        reason = $"ON: SOC={data.BatterySoc}% (turn off <= {GetSocTurnOffThreshold(rule)}%)";
                    }
                    else if (data.BatterySoc < rule.SocTurnOnThreshold)
                    {
                        conditionKey = "no-change:off:soc-below-threshold";
                        reason = $"OFF: SOC={data.BatterySoc}% (need >= {rule.SocTurnOnThreshold}%)";
                    }
                    else if (TryDescribeCooldown(rule, DateTimeOffset.UtcNow, out var cooldownReason))
                    {
                        conditionKey = "no-change:off:cooldown";
                        reason = cooldownReason;
                    }
                    else if (ShouldDescribeBlockedSolarProduction(data, rule, evaluationContext))
                    {
                        conditionKey = GetBlockedSolarProductionConditionKey(evaluationContext);
                        reason = $"OFF: {DescribeSolarProductionCondition(data, rule, evaluationContext)}";
                    }
                    else
                    {
                        conditionKey = "no-change:off:conditions-not-met";
                        reason = "OFF: conditions not met";
                    }
                }

                await UpsertRuleRunLogAsync(db, rule.Name, action, conditionKey, reason, data, ct);
            }

            await db.SaveChangesAsync(ct);

            // Cleanup data older than 3 days
            var cutoff = DateTime.UtcNow.AddDays(-3);
            var old = db.RuleRunLogs.Where(r => r.Timestamp < cutoff);
            db.RuleRunLogs.RemoveRange(old);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to log rule runs");
        }
    }

    private static int GetSocTurnOffThreshold(TriggerRule rule)
        => rule.UseSeparateSocTurnOffThreshold ? rule.SocTurnOffThreshold : rule.SocTurnOnThreshold;

    private static async Task UpsertRuleRunLogAsync(
        DeyeSolarDbContext db,
        string ruleName,
        string action,
        string conditionKey,
        string reason,
        InverterData data,
        CancellationToken ct)
    {
        conditionKey = NormalizeConditionKey(conditionKey);

        var latest = await db.RuleRunLogs
            .Where(r => r.RuleName == ruleName)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);

        if (latest != null &&
            latest.Action == action &&
            latest.ConditionKey == conditionKey)
        {
            latest.Timestamp = DateTime.UtcNow;
            latest.Reason = reason;
            latest.BatterySoc = data.BatterySoc;
            latest.SolarProduction = data.SolarProduction;
            latest.BatteryPower = data.BatteryPower;
            return;
        }

        db.RuleRunLogs.Add(new RuleRunLog
        {
            Timestamp = DateTime.UtcNow,
            RuleName = ruleName,
            Action = action,
            ConditionKey = conditionKey,
            Reason = reason,
            BatterySoc = data.BatterySoc,
            SolarProduction = data.SolarProduction,
            BatteryPower = data.BatteryPower
        });
    }

    private static string NormalizeConditionKey(string conditionKey)
        => conditionKey.Length <= 160 ? conditionKey : conditionKey[..160];

    private static bool TryDescribeCooldown(TriggerRule rule, DateTimeOffset now, out string reason)
    {
        reason = string.Empty;
        if (!rule.CurrentStateChangedAt.HasValue)
            return false;

        var changedAt = new DateTimeOffset(rule.CurrentStateChangedAt.Value, TimeSpan.Zero);
        var elapsed = now - changedAt;
        var cooldown = TimeSpan.FromMinutes(rule.CooldownMinutes);
        if (elapsed >= cooldown)
            return false;

        var remaining = cooldown - elapsed;
        reason = $"OFF: cooldown active ({Math.Ceiling(remaining.TotalMinutes)} min remaining)";
        return true;
    }

    public async Task<RuleEvaluationContext> BuildContextAsync(DateTime now, string deviceKey, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var cutoff = now.AddMinutes(-RuleEvaluator.SolarProductionAverageWindowMinutes);

        var averageSolar = await db.Readings
            .Where(r => r.Timestamp >= cutoff && r.SolarObservedAt >= cutoff && r.BatterySocValid != false
                && r.SolarDeviceSn == deviceKey &&
                r.BatterySoc < RuleEvaluator.SolarProductionBypassSocThreshold)
            .AverageAsync(r => (double?)r.SolarProduction, ct);

        return new RuleEvaluationContext(
            averageSolar.HasValue ? (int)Math.Round(averageSolar.Value) : null);
    }

    private static bool ShouldDescribeBlockedSolarProduction(
        InverterData data,
        TriggerRule rule,
        RuleEvaluationContext context)
        => rule.UseSolarProductionThreshold &&
            data.BatterySoc >= rule.SocTurnOnThreshold &&
            data.BatterySoc < RuleEvaluator.SolarProductionBypassSocThreshold &&
            !RuleEvaluator.IsSolarProductionConditionSatisfied(data, rule, context);

    private static string GetBlockedSolarProductionConditionKey(RuleEvaluationContext context)
        => context.AverageSolarProductionWatts.HasValue
            ? "no-change:off:solar-average-below-threshold"
            : "no-change:off:solar-average-unavailable";

    private static string DescribeSolarProductionCondition(
        InverterData data,
        TriggerRule rule,
        RuleEvaluationContext context)
    {
        if (!rule.UseSolarProductionThreshold)
            return string.Empty;

        if (data.BatterySoc >= RuleEvaluator.SolarProductionBypassSocThreshold)
            return $"PV avg threshold bypassed at SOC >= {RuleEvaluator.SolarProductionBypassSocThreshold}%";

        var averageWatts = context.AverageSolarProductionWatts;
        if (!averageWatts.HasValue)
        {
            return $"PV avg last {RuleEvaluator.SolarProductionAverageWindowMinutes}m unavailable while SOC < {RuleEvaluator.SolarProductionBypassSocThreshold}%";
        }

        return $"PV avg last {RuleEvaluator.SolarProductionAverageWindowMinutes}m={averageWatts.Value}W (need >= {rule.MinAverageSolarProductionWatts}W while SOC < {RuleEvaluator.SolarProductionBypassSocThreshold}%)";
    }

    public async Task CleanupAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            await ExpiredReadings(db.Readings, DateTime.UtcNow).ExecuteDeleteAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to clean up expired readings");
        }
    }

    internal static IQueryable<Reading> ExpiredReadings(IQueryable<Reading> readings, DateTime now)
    {
        // Retain the full monthly chart window plus its leading integration sample.
        var cutoff = now.AddDays(-HistoryRetentionDays);
        return readings.Where(r => r.Timestamp < cutoff);
    }
}
