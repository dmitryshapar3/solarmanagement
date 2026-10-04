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

/// <summary>Schedules cycles. Source routing, recovery and decision execution belong to separate services.</summary>
public class PollingWorker : BackgroundService
{
    private readonly IRulePollingCycle _cycle;
    private readonly IOptionsMonitor<PollingOptions> _pollingOptions;
    private readonly ILogger<PollingWorker> _logger;
    public PollingWorker(IRulePollingCycle cycle, IOptionsMonitor<PollingOptions> pollingOptions, ILogger<PollingWorker> logger)
    { _cycle = cycle; _pollingOptions = pollingOptions; _logger = logger; }

    // Explicit compatibility composition for existing integration fixtures; production DI uses the cycle port.
    public PollingWorker(IInverterRefreshService inverterRefresh, IOptionsMonitor<InverterConnectionOptions> inverterOptions,
        ISocketController socketController, IRuleRepository ruleRepository, RuleEvaluator ruleEvaluator,
        IDbContextFactory<DeyeSolarDbContext> dbFactory, IOptionsMonitor<PollingOptions> pollingOptions,
        IAppSettingsReader settingsService, ILogger<PollingWorker> logger,
        IInverterDataSource? sources = null, ExportReadingStore? readings = null)
        : this(CreateFixtureCycle(inverterRefresh, inverterOptions, socketController, ruleRepository, ruleEvaluator,
            dbFactory, settingsService, logger, sources, readings), pollingOptions, logger) { }

    private static IRulePollingCycle CreateFixtureCycle(IInverterRefreshService inverterRefresh,
        IOptionsMonitor<InverterConnectionOptions> inverterOptions, ISocketController socketController,
        IRuleRepository repository, RuleEvaluator evaluator, IDbContextFactory<DeyeSolarDbContext> factory,
        IAppSettingsReader settings, ILogger logger, IInverterDataSource? sources, ExportReadingStore? readings)
    {
        var history = new RuleRunHistory(factory, logger);
        var executor = new RuleAutomationExecutor(socketController, repository, evaluator, settings, history,
            new RuleObservationReconciler(factory, socketController), logger);
        return new RulePollingCycle(inverterRefresh, inverterOptions, repository, factory, history, executor,
            new SocketReceiptReconciler(factory, socketController as SolarManagement.SmartSockets.Contracts.ISocketCommandTracker, logger),
            logger, sources, readings);
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

    internal Task PollAndEvaluateAsync(CancellationToken ct) => _cycle.PollAndEvaluateAsync(ct);
    internal static IQueryable<Reading> ExpiredReadings(IQueryable<Reading> readings, DateTime now)
        => RuleRunHistory.ExpiredReadings(readings, now);
}
