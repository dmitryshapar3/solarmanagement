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
    private readonly IRuleRepository _ruleRepository;
    private readonly InverterDataSnapshot _snapshot;
    private readonly RuleEvaluator _ruleEvaluator;
    private readonly IDbContextFactory<DeyeSolarDbContext> _dbFactory;
    private readonly IOptionsMonitor<PollingOptions> _pollingOptions;
    private readonly ILogger<PollingWorker> _logger;

    public PollingWorker(
        IInverterDataSource dataSource,
        ISocketController socketController,
        IRuleRepository ruleRepository,
        InverterDataSnapshot snapshot,
        RuleEvaluator ruleEvaluator,
        IDbContextFactory<DeyeSolarDbContext> dbFactory,
        IOptionsMonitor<PollingOptions> pollingOptions,
        ILogger<PollingWorker> logger)
    {
        _dataSource = dataSource;
        _socketController = socketController;
        _ruleRepository = ruleRepository;
        _snapshot = snapshot;
        _ruleEvaluator = ruleEvaluator;
        _dbFactory = dbFactory;
        _pollingOptions = pollingOptions;
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
        _logger.LogDebug("Read data: SOC={Soc}%, Solar={Solar}W, Battery={Battery}W",
            data.BatterySoc, data.SolarProduction, data.BatteryPower);

        _snapshot.Update(data);

        await SaveReadingAsync(data, ct);

        var rules = await _ruleRepository.GetAllAsync(ct);
        var actions = _ruleEvaluator.Evaluate(data, rules, DateTimeOffset.Now);

        foreach (var action in actions)
        {
            try
            {
                if (action.TurnOn)
                    await _socketController.TurnOnAsync(action.EntityId, ct);
                else
                    await _socketController.TurnOffAsync(action.EntityId, ct);

                var rule = rules.First(r => r.Id == action.RuleId);
                rule.CurrentState = action.TurnOn;
                rule.LastTriggered = DateTimeOffset.Now;
                await _ruleRepository.UpdateAsync(rule, ct);

                _logger.LogInformation("Rule '{RuleName}' triggered: {Action} {EntityId}",
                    rule.Name, action.TurnOn ? "ON" : "OFF", action.EntityId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute action for rule {RuleId}", action.RuleId);
            }
        }
    }

    private async Task SaveReadingAsync(InverterData data, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            db.Readings.Add(new Reading
            {
                Timestamp = data.Timestamp,
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

            var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
            await db.Readings.Where(r => r.Timestamp < cutoff).ExecuteDeleteAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save reading to database");
        }
    }
}
