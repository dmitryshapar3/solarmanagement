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

public interface IRulePollingCycle
{
    Task PollAndEvaluateAsync(CancellationToken ct);
}

/// <summary>Routes source observations and due rules to the decision executor; owns no scheduler loop.</summary>
internal sealed class RulePollingCycle : IRulePollingCycle
{
    private readonly IInverterRefreshService _inverterRefresh;
    private readonly IOptionsMonitor<InverterConnectionOptions> _inverterOptions;
    private readonly IRuleRepository _ruleRepository;
    private readonly IDbContextFactory<DeyeSolarDbContext> _dbFactory;
    private readonly ILogger _logger;
    private readonly IInverterDataSource? _sources;
    private readonly ExportReadingStore? _readings;
    private readonly IRuleRunHistory _history;
    private readonly IRuleAutomationExecutor _executor;
    private readonly ISocketReceiptReconciler _receipts;

    public RulePollingCycle(
        IInverterRefreshService inverterRefresh,
        IOptionsMonitor<InverterConnectionOptions> inverterOptions,
        IRuleRepository ruleRepository,
        IDbContextFactory<DeyeSolarDbContext> dbFactory,
        IRuleRunHistory history, IRuleAutomationExecutor executor, ISocketReceiptReconciler receipts,
        ILogger logger, IInverterDataSource? sources = null, ExportReadingStore? readings = null)
    {
        _inverterRefresh = inverterRefresh;
        _inverterOptions = inverterOptions;
        _ruleRepository = ruleRepository;
        _dbFactory = dbFactory;
        _logger = logger;
        _sources = sources;
        _readings = readings;
        _history = history;
        _executor = executor;
        _receipts = receipts;
    }

    public async Task PollAndEvaluateAsync(CancellationToken ct)
    {
        await _history.CleanupAsync(ct);
        await _receipts.ReconcileAsync(ct);
        var now = DateTime.UtcNow;
        InverterData? primary = null;
        try { primary = await _inverterRefresh.RefreshAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _logger.LogWarning("Primary inverter is unavailable ({ErrorType})", ex.GetType().Name); }
        var primaryIdentity = InverterRefreshIdentity.Capture(_inverterOptions.CurrentValue);
        var allRules = await _ruleRepository.GetAllAsync(ct);
        var enabled = allRules.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.EntityId)).ToList();
        Dictionary<int, string> targets = [];
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
            foreach (var rule in enabled) targets[rule.Id] = await RuleTargetPolicy.IdentityAsync(db, rule.EntityId, ct);
        var conflicts = targets.GroupBy(target => target.Value).Where(group => group.Count() > 1)
            .SelectMany(group => group.Select(target => target.Key)).ToHashSet();
        var due = enabled.Where(r => !conflicts.Contains(r.Id) && IsDueForEvaluation(r, now)).ToList();
        Dictionary<int, Guid?> effectiveSources;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
            effectiveSources = await IntegrationSocketAssociation.ResolveSourcesAsync(db, due, ct);
        foreach (var group in due.GroupBy(r => effectiveSources.GetValueOrDefault(r.Id)))
        {
            try
            {
                var data = primary;
                if (group.Key is { } sourceId && data?.InverterId != sourceId)
                {
                    data = null;
                    if (_sources is IRegisteredInverterDataSource sources && _readings is not null)
                    {
                        try
                        {
                            data = await PollingRetryPolicy.ExecuteAsync(token => sources.ReadDeviceAsync(new(sourceId), token), ct);
                            await _readings.SavePollingAsync(data, ct);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex) { _logger.LogWarning("Source inverter is unavailable ({ErrorType})", ex.GetType().Name); }
                    }
                }
                var identity = primaryIdentity;
                async Task<bool> IsCurrent()
                {
                    if (group.Key is null && !identity.Matches(_inverterOptions.CurrentValue)) return false;
                    if (data is not null && _sources is IRegisteredInverterDataSource registered)
                    { if (!await registered.IsCurrentAsync(data, ct)) return false; }
                    else if (data is not null && !identity.Matches(_inverterOptions.CurrentValue)) return false;
                    await using var db = await _dbFactory.CreateDbContextAsync(ct);
                    var ids = group.Select(rule => rule.Id).ToArray();
                    var currentRules = await db.TriggerRules.AsNoTracking().Where(rule => ids.Contains(rule.Id)).ToListAsync(ct);
                    if (group.Any(rule => !currentRules.Any(current => current.Id == rule.Id
                        && IntegrationAutomationSourceGuard.SameConfiguration(rule, current)))) return false;
                    var currentSources = await IntegrationSocketAssociation.ResolveSourcesAsync(db, currentRules, ct);
                    return currentRules.All(rule => currentSources.GetValueOrDefault(rule.Id) == group.Key);
                }
                await _executor.EvaluateAsync(data, group.ToList(), group.Key, now, IsCurrent, _inverterOptions.CurrentValue.DeviceKey, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.LogWarning("Inverter rule evaluation is unavailable ({ErrorType})", ex.GetType().Name); }
        }
    }
    private static bool IsDueForEvaluation(TriggerRule rule, DateTime now)
    {
        if (!rule.LastEvaluated.HasValue)
            return true;
        return (now - rule.LastEvaluated.Value).TotalSeconds >= rule.IntervalSeconds;
    }

}
