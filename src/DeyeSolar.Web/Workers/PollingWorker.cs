using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using DeyeSolar.RuleEngine;
using DeyeSolar.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Web.Workers;

public class PollingWorker : BackgroundService
{
    private readonly IInverterDataSource _dataSource;
    private readonly ISocketController _socketController;
    private readonly ISocketInventoryService _socketInventoryService;
    private readonly IRuleRepository _ruleRepository;
    private readonly InverterDataSnapshot _snapshot;
    private readonly DeviceStatusSnapshot _deviceStatusSnapshot;
    private readonly RuleEvaluator _ruleEvaluator;
    private readonly IDbContextFactory<DeyeSolarDbContext> _dbFactory;
    private readonly IOptionsMonitor<PollingOptions> _pollingOptions;
    private readonly AppSettingsService _settingsService;
    private readonly ILogger<PollingWorker> _logger;

    public PollingWorker(
        IInverterDataSource dataSource,
        ISocketController socketController,
        ISocketInventoryService socketInventoryService,
        IRuleRepository ruleRepository,
        InverterDataSnapshot snapshot,
        DeviceStatusSnapshot deviceStatusSnapshot,
        RuleEvaluator ruleEvaluator,
        IDbContextFactory<DeyeSolarDbContext> dbFactory,
        IOptionsMonitor<PollingOptions> pollingOptions,
        AppSettingsService settingsService,
        ILogger<PollingWorker> logger)
    {
        _dataSource = dataSource;
        _socketController = socketController;
        _socketInventoryService = socketInventoryService;
        _ruleRepository = ruleRepository;
        _snapshot = snapshot;
        _deviceStatusSnapshot = deviceStatusSnapshot;
        _ruleEvaluator = ruleEvaluator;
        _dbFactory = dbFactory;
        _pollingOptions = pollingOptions;
        _settingsService = settingsService;
        _logger = logger;
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Polling cycle failed");
            }

            var interval = TimeSpan.FromSeconds(_pollingOptions.CurrentValue.IntervalSeconds);
            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task PollAndEvaluateAsync(CancellationToken ct)
    {
        var data = await _dataSource.ReadCurrentDataAsync(ct);
        _logger.LogInformation("Poll: SOC={Soc}%, Solar={Solar}W, BatteryPower={Battery}W, Load={Load}W",
            data.BatterySoc, data.SolarProduction, data.BatteryPower, data.LoadPower);

        _snapshot.Update(data);
        await SaveReadingAsync(data, ct);
        await RefreshDeviceStatusesAsync(ct);

        var allRules = await _ruleRepository.GetAllAsync(ct);
        var now = DateTime.UtcNow;

        // Filter to rules that are due for evaluation based on their interval
        var dueRules = allRules.Where(r => r.Enabled && IsDueForEvaluation(r, now)).ToList();

        if (dueRules.Count == 0)
        {
            _logger.LogDebug("No rules due for evaluation");
            return;
        }

        // Skip rules without a device assigned
        dueRules = dueRules.Where(r => !string.IsNullOrEmpty(r.EntityId)).ToList();
        if (dueRules.Count == 0)
            return;

        _logger.LogInformation("Evaluating {Count} rule(s)", dueRules.Count);

        // Sync actual device state for due rules
        await SyncDeviceStateAsync(dueRules, ct);

        var displayOpts = await _settingsService.LoadSectionAsync<DeyeSolar.Domain.Options.DisplayOptions>("Display");
        var actions = _ruleEvaluator.Evaluate(data, dueRules, DateTimeOffset.Now, displayOpts.TimeZoneId);
        var successfulActions = new HashSet<int>();
        var failedActions = new Dictionary<int, string>();

        foreach (var action in actions)
        {
            var rule = dueRules.First(r => r.Id == action.RuleId);
            try
            {
                if (action.TurnOn)
                    await _socketController.TurnOnAsync(action.EntityId, ct);
                else
                    await _socketController.TurnOffAsync(action.EntityId, ct);

                rule.CurrentState = action.TurnOn;
                rule.LastEvaluated = now;
                await _ruleRepository.UpdateAsync(rule, ct);
                successfulActions.Add(action.RuleId);

                _logger.LogInformation("Rule '{RuleName}' triggered: {Action} {EntityId}",
                    rule.Name, action.TurnOn ? "ON" : "OFF", action.EntityId);
            }
            catch (Exception ex)
            {
                rule.LastEvaluated = now;
                await _ruleRepository.UpdateAsync(rule, ct);
                failedActions[action.RuleId] = $"{(action.TurnOn ? "ON" : "OFF")} failed: {ex.Message}";
                _logger.LogError(ex, "Failed to execute action for rule {RuleId}", action.RuleId);
            }
        }

        // Update LastEvaluated for rules that had no action (still evaluated, just no change)
        foreach (var rule in dueRules)
        {
            if (!actions.Any(a => a.RuleId == rule.Id))
            {
                rule.LastEvaluated = now;
                await _ruleRepository.UpdateAsync(rule, ct);
            }
        }

        await LogRuleRunsAsync(data, dueRules, actions, successfulActions, failedActions, ct);
    }

    private static bool IsDueForEvaluation(TriggerRule rule, DateTime now)
    {
        if (!rule.LastEvaluated.HasValue)
            return true;
        return (now - rule.LastEvaluated.Value).TotalSeconds >= rule.IntervalSeconds;
    }

    private async Task SyncDeviceStateAsync(List<TriggerRule> rules, CancellationToken ct)
    {
        foreach (var rule in rules.Where(r => r.Enabled && !string.IsNullOrEmpty(r.EntityId)))
        {
            try
            {
                var actualState = await _socketController.GetStateAsync(rule.EntityId, ct);
                if (rule.CurrentState != actualState)
                {
                    _logger.LogInformation("Rule '{Name}' state synced: {Old} -> {New}",
                        rule.Name, rule.CurrentState ? "ON" : "OFF", actualState ? "ON" : "OFF");
                    rule.CurrentState = actualState;
                    await _ruleRepository.UpdateAsync(rule, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not sync device state for rule {RuleId}", rule.Id);
            }
        }
    }

    private async Task LogRuleRunsAsync(
        InverterData data,
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

                if (actionsByRule.TryGetValue(rule.Id, out var ruleAction) &&
                    successfulActions.Contains(rule.Id))
                {
                    action = ruleAction.TurnOn ? "ON" : "OFF";
                    var turnOffThreshold = GetSocTurnOffThreshold(rule);
                    if (ruleAction.TurnOn)
                    {
                        reason = $"SOC={data.BatterySoc}% >= {rule.SocTurnOnThreshold}%, cooldown elapsed";
                    }
                    else if (data.BatterySoc <= turnOffThreshold)
                    {
                        reason = $"SOC={data.BatterySoc}% <= turn-off threshold {turnOffThreshold}%";
                    }
                    else
                    {
                        reason = "Outside active time window";
                    }
                }
                else if (failedActions.TryGetValue(rule.Id, out var failureReason))
                {
                    action = "ERROR";
                    reason = failureReason;
                }
                else
                {
                    action = "NO_CHANGE";
                    if (rule.CurrentState)
                    {
                        reason = $"ON: SOC={data.BatterySoc}% (turn off <= {GetSocTurnOffThreshold(rule)}%)";
                    }
                    else
                    {
                        reason = $"OFF: SOC={data.BatterySoc}% (need >={rule.SocTurnOnThreshold}%)";
                    }
                }

                db.RuleRunLogs.Add(new RuleRunLog
                {
                    Timestamp = DateTime.UtcNow,
                    RuleName = rule.Name,
                    Action = action,
                    Reason = reason,
                    BatterySoc = data.BatterySoc,
                    SolarProduction = data.SolarProduction,
                    BatteryPower = data.BatteryPower
                });
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

    private async Task RefreshDeviceStatusesAsync(CancellationToken ct)
    {
        try
        {
            var devices = await _socketInventoryService.GetCachedDevicesAsync(ct);
            _deviceStatusSnapshot.Update(devices);
            _logger.LogDebug("Refreshed status for {Count} smart socket(s)", devices.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh smart socket statuses");
        }
    }

    private async Task SaveReadingAsync(InverterData data, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            db.Readings.Add(new Reading
            {
                Timestamp = DateTime.UtcNow,
                BatterySoc = data.BatterySoc,
                BatteryTemperature = data.BatteryTemperature,
                BatteryVoltage = data.BatteryVoltage,
                BatteryPower = data.BatteryPower,
                BatteryCurrent = data.BatteryCurrent,
                SolarProduction = data.SolarProduction,
                GridConsumption = data.GridConsumption,
                LoadPower = data.LoadPower,
                DataSource = "DeyeCloud"
            });
            await db.SaveChangesAsync(ct);

            // Cleanup readings older than 3 days
            var cutoff = DateTime.UtcNow.AddDays(-3);
            var oldReadings = db.Readings.Where(r => r.Timestamp < cutoff);
            db.Readings.RemoveRange(oldReadings);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save reading to database");
        }
    }
}
