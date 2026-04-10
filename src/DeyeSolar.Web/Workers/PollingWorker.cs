using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using DeyeSolar.Infrastructure.Tuya;
using DeyeSolar.RuleEngine;
using DeyeSolar.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Web.Workers;

public class PollingWorker : BackgroundService
{
    private readonly IInverterDataSource _dataSource;
    private readonly ISocketController _socketController;
    private readonly TuyaCloudClient _tuyaClient;
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
        TuyaCloudClient tuyaClient,
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
        _tuyaClient = tuyaClient;
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

        // Load enough readings for the largest drain window across all rules
        var allRules = await _ruleRepository.GetAllAsync(ct);
        var maxWindow = allRules.Where(r => r.Enabled).Select(r => r.DrainWindowMinutes).DefaultIfEmpty(15).Max();
        var recentReadings = await GetRecentReadingsAsync(maxWindow + 5, ct);
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

        _logger.LogInformation("Evaluating {Count} rule(s), {Readings} recent readings available",
            dueRules.Count, recentReadings.Count);

        // Sync actual device state for due rules
        await SyncDeviceStateAsync(dueRules, ct);

        var displayOpts = await _settingsService.LoadSectionAsync<DeyeSolar.Domain.Options.DisplayOptions>("Display");
        var actions = _ruleEvaluator.Evaluate(data, recentReadings, dueRules, DateTimeOffset.Now, displayOpts.TimeZoneId);

        await LogRuleRunsAsync(data, recentReadings, dueRules, actions, ct);

        foreach (var action in actions)
        {
            try
            {
                if (action.TurnOn)
                    await _socketController.TurnOnAsync(action.EntityId, ct);
                else
                    await _socketController.TurnOffAsync(action.EntityId, ct);

                var rule = dueRules.First(r => r.Id == action.RuleId);
                rule.CurrentState = action.TurnOn;
                rule.LastEvaluated = now;
                await _ruleRepository.UpdateAsync(rule, ct);

                _logger.LogInformation("Rule '{RuleName}' triggered: {Action} {EntityId}",
                    rule.Name, action.TurnOn ? "ON" : "OFF", action.EntityId);
            }
            catch (Exception ex)
            {
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
        IReadOnlyList<InverterData> recentReadings,
        List<TriggerRule> rules,
        IReadOnlyList<RuleAction> actions,
        CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var actionsByRule = actions.ToDictionary(a => a.RuleId);
            var nowOffset = DateTimeOffset.UtcNow;

            foreach (var rule in rules.Where(r => r.Enabled))
            {
                var drainWh = RuleEvaluator.CalculateNetBatteryDrainWh(recentReadings, rule.DrainWindowMinutes, nowOffset);

                string action;
                string reason;

                var anchor = rule.SocAtDrainStart?.ToString() ?? "-";
                var drop = rule.SocAtDrainStart.HasValue ? rule.SocAtDrainStart.Value - data.BatterySoc : 0;

                if (actionsByRule.TryGetValue(rule.Id, out var ruleAction))
                {
                    action = ruleAction.TurnOn ? "ON" : "OFF";
                    if (ruleAction.TurnOn)
                    {
                        reason = $"SOC={data.BatterySoc}% >= {rule.SocTurnOnThreshold}%, cooldown elapsed";
                    }
                    else if (data.BatterySoc <= rule.SocFloor)
                    {
                        reason = $"SOC floor hit: {data.BatterySoc}% <= {rule.SocFloor}%";
                    }
                    else if (rule.SocAtDrainStart.HasValue && drop >= rule.MaxSocDropPercent)
                    {
                        reason = $"SOC drop {drop}% >= cap {rule.MaxSocDropPercent}% (anchor {anchor}% → {data.BatterySoc}%)";
                    }
                    else
                    {
                        reason = $"Net drain {drainWh:F0}Wh >= {rule.MaxDrainWh}Wh over {rule.DrainWindowMinutes}min";
                    }
                }
                else
                {
                    action = "NO_CHANGE";
                    if (rule.CurrentState)
                    {
                        reason = $"ON: SOC={data.BatterySoc}%, anchor={anchor}%, drop={drop}%/{rule.MaxSocDropPercent}%, drain={drainWh:F0}Wh/{rule.MaxDrainWh}Wh, floor={rule.SocFloor}%";
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

    private async Task RefreshDeviceStatusesAsync(CancellationToken ct)
    {
        try
        {
            var devices = await _tuyaClient.GetDevicesWithStatusAsync(ct);
            _deviceStatusSnapshot.Update(devices);
            _logger.LogDebug("Refreshed status for {Count} Tuya device(s)", devices.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh Tuya device statuses");
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

    private async Task<IReadOnlyList<InverterData>> GetRecentReadingsAsync(int minutes, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var cutoff = DateTime.UtcNow.AddMinutes(-minutes);
            var readings = await db.Readings
                .Where(r => r.Timestamp >= cutoff)
                .OrderBy(r => r.Timestamp)
                .ToListAsync(ct);

            return readings.Select(r => new InverterData
            {
                BatterySoc = r.BatterySoc,
                BatteryPower = r.BatteryPower,
                BatteryVoltage = r.BatteryVoltage,
                BatteryTemperature = r.BatteryTemperature,
                BatteryCurrent = r.BatteryCurrent,
                SolarProduction = r.SolarProduction,
                GridConsumption = r.GridConsumption,
                LoadPower = r.LoadPower,
                Timestamp = new DateTimeOffset(r.Timestamp, TimeSpan.Zero)
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load recent readings");
            return Array.Empty<InverterData>();
        }
    }
}
