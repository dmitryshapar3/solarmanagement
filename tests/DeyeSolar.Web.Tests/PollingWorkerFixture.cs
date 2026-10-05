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
using Microsoft.Extensions.Logging;

using DeyeSolar.Web.Workers;

namespace DeyeSolar.Web.Tests;

internal static class PollingWorkerFixture
{
    public static PollingWorker Create(IInverterRefreshService inverterRefresh, IOptionsMonitor<InverterConnectionOptions> inverterOptions,
        ISocketController socketController, IRuleRepository ruleRepository, RuleEvaluator ruleEvaluator,
        IDbContextFactory<DeyeSolarDbContext> dbFactory, IOptionsMonitor<PollingOptions> pollingOptions,
        IAppSettingsReader settingsService, ILogger<PollingWorker> logger,
        IInverterDataSource? sources = null, ExportReadingStore? readings = null)
        => new(CreateFixtureCycle(inverterRefresh, inverterOptions, socketController, ruleRepository, ruleEvaluator,
            dbFactory, settingsService, logger, sources, readings), pollingOptions, logger);

    private static IRulePollingCycle CreateFixtureCycle(IInverterRefreshService inverterRefresh,
        IOptionsMonitor<InverterConnectionOptions> inverterOptions, ISocketController socketController,
        IRuleRepository repository, RuleEvaluator evaluator, IDbContextFactory<DeyeSolarDbContext> factory,
        IAppSettingsReader settings, ILogger logger, IInverterDataSource? sources, ExportReadingStore? readings)
    {
        var history = new RuleRunHistory(factory, logger, TimeProvider.System);
        var executor = new RuleAutomationExecutor(socketController, repository, evaluator, settings, history,
            new RuleObservationReconciler(factory, socketController), logger);
        return new RulePollingCycle(inverterRefresh, inverterOptions, repository, factory, history, executor,
            new SocketReceiptReconciler(factory, socketController as SolarManagement.SmartSockets.Contracts.ISocketCommandTracker, logger),
            logger, sources, readings);
    }

}
