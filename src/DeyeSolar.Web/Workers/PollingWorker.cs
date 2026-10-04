using SolarManagement.Inverters.Contracts;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using DeyeSolar.RuleEngine;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Web.Workers;

public class PollingWorker : BackgroundService
{
    private const int HistoryRetentionDays = 31;
    private readonly IInverterRefreshService _inverterRefresh;
    private readonly IOptionsMonitor<InverterConnectionOptions> _inverterOptions;
    private readonly ISocketController _socketController;
    private readonly IRuleRepository _ruleRepository;
    private readonly RuleEvaluator _ruleEvaluator;
    private readonly IDbContextFactory<DeyeSolarDbContext> _dbFactory;
    private readonly IOptionsMonitor<PollingOptions> _pollingOptions;
    private readonly AppSettingsService _settingsService;
    private readonly ILogger<PollingWorker> _logger;
    private readonly IInverterDataSource? _sources;
    private readonly ExportReadingStore? _readings;

    public PollingWorker(
        IInverterRefreshService inverterRefresh,
        IOptionsMonitor<InverterConnectionOptions> inverterOptions,
        ISocketController socketController,
        IRuleRepository ruleRepository,
        RuleEvaluator ruleEvaluator,
        IDbContextFactory<DeyeSolarDbContext> dbFactory,
        IOptionsMonitor<PollingOptions> pollingOptions,
        AppSettingsService settingsService,
        ILogger<PollingWorker> logger, IInverterDataSource? sources = null, ExportReadingStore? readings = null)
    {
        _inverterRefresh = inverterRefresh;
        _inverterOptions = inverterOptions;
        _socketController = socketController;
        _ruleRepository = ruleRepository;
        _ruleEvaluator = ruleEvaluator;
        _dbFactory = dbFactory;
        _pollingOptions = pollingOptions;
        _settingsService = settingsService;
        _logger = logger;
        _sources = sources;
        _readings = readings;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PollingWorker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAndEvaluateAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Polling cycle failed");
            }

            var interval = TimeSpan.FromSeconds(_pollingOptions.CurrentValue.IntervalSeconds);
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal async Task PollAndEvaluateAsync(CancellationToken ct)
    {
        await CleanupReadingsAsync(ct);
        var now = DateTime.UtcNow;
        InverterData? primary = null;
        try { primary = await _inverterRefresh.RefreshAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _logger.LogWarning("Primary inverter is unavailable ({ErrorType})", ex.GetType().Name); }
        var primaryIdentity = InverterRefreshIdentity.Capture(_inverterOptions.CurrentValue);
        var allRules = await _ruleRepository.GetAllAsync(ct);
        var due = allRules.Where(r => r.Enabled && IsDueForEvaluation(r, now) && !string.IsNullOrEmpty(r.EntityId)).ToList();
        foreach (var group in due.GroupBy(r => r.SourceInverterId))
        {
            try
            {
                var data = primary;
                if (group.Key is { } sourceId && data?.InverterId != sourceId)
                {
                    if (_sources is not IRegisteredInverterDataSource sources || _readings is null) continue;
                    data = await sources.ReadDeviceAsync(new(sourceId), ct);
                    await _readings.SavePollingAsync(data, ct);
                }
                if (data is null) continue;
                var identity = primaryIdentity;
                Task<bool> IsCurrent() => group.Key is null && !identity.Matches(_inverterOptions.CurrentValue)
                    ? Task.FromResult(false) : _sources is IRegisteredInverterDataSource registered
                        ? registered.IsCurrentAsync(data, ct) : Task.FromResult(identity.Matches(_inverterOptions.CurrentValue));
                await EvaluateSourceAsync(data, group.ToList(), now, IsCurrent, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.LogWarning("Inverter rule evaluation is unavailable ({ErrorType})", ex.GetType().Name); }
        }
    }
    private async Task EvaluateSourceAsync(InverterData data, List<TriggerRule> dueRules, DateTime now,
        Func<Task<bool>> isCurrent, CancellationToken ct)
    {
        if (!await isCurrent()) return;
        _logger.LogInformation("Evaluating {Count} rule(s)", dueRules.Count);
        var evaluationContext = await BuildEvaluationContextAsync(now, data.InverterId?.ToString("D") ?? _inverterOptions.CurrentValue.DeviceKey, ct);

        var displayOpts = await _settingsService.LoadSectionAsync<DeyeSolar.Domain.Options.DisplayOptions>("Display");
        if (!await isCurrent()) return;
        var actions = _ruleEvaluator.Evaluate(data, dueRules, DateTimeOffset.Now, displayOpts.TimeZoneId, evaluationContext);
        var successfulActions = new HashSet<int>();
        var failedActions = new Dictionary<int, string>();
        var recordedRules = new HashSet<int>();

        foreach (var action in actions)
        {
            if (!await isCurrent()) break;
            var rule = dueRules.First(r => r.Id == action.RuleId);
            try
            {
                if (action.TurnOn)
                    await _socketController.TurnOnAsync(action.EntityId, ct);
                else
                    await _socketController.TurnOffAsync(action.EntityId, ct);

                rule.CurrentState = action.TurnOn;
                rule.LastEvaluated = now;
                await _ruleRepository.RecordEvaluationAsync(rule.Id, now, ct);
                successfulActions.Add(action.RuleId);
                recordedRules.Add(action.RuleId);

                _logger.LogInformation("Rule '{RuleName}' triggered: {Action} {EntityId}",
                    rule.Name, action.TurnOn ? "ON" : "OFF", action.EntityId);
            }
            catch (Exception ex)
            {
                rule.LastEvaluated = now;
                await _ruleRepository.RecordEvaluationAsync(rule.Id, now, ct);
                failedActions[action.RuleId] = $"{(action.TurnOn ? "ON" : "OFF")} failed: {ex.Message}";
                recordedRules.Add(action.RuleId);
                _logger.LogError(ex, "Failed to execute action for rule {RuleId}", action.RuleId);
            }
        }

        // Update LastEvaluated for rules that had no action (still evaluated, just no change)
        foreach (var rule in dueRules)
        {
            if (!await isCurrent()) break;
            if (data.BatterySocValid != false && !actions.Any(a => a.RuleId == rule.Id))
            {
                rule.LastEvaluated = now;
                await _ruleRepository.RecordEvaluationAsync(rule.Id, now, ct);
                recordedRules.Add(rule.Id);
            }
        }

        if (recordedRules.Count > 0)
            await LogRuleRunsAsync(data, evaluationContext, dueRules.Where(rule => recordedRules.Contains(rule.Id)).ToList(),
                actions, successfulActions, failedActions, ct);
    }

    private static bool IsDueForEvaluation(TriggerRule rule, DateTime now)
    {
        if (!rule.LastEvaluated.HasValue)
            return true;
        return (now - rule.LastEvaluated.Value).TotalSeconds >= rule.IntervalSeconds;
    }

    private async Task LogRuleRunsAsync(
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

    private async Task<RuleEvaluationContext> BuildEvaluationContextAsync(DateTime now, string deviceKey, CancellationToken ct)
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

    private async Task CleanupReadingsAsync(CancellationToken ct)
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
