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

/// <summary>Reconciles observations, plans decisions and executes them through the fenced command coordinator.</summary>
internal sealed class RuleAutomationExecutor(ISocketController _socketController, IRuleRepository _ruleRepository,
    IRuleDecisionEvaluator _ruleEvaluator, IAppSettingsReader _settingsService, IRuleRunHistory _history,
    IRuleObservationReconciler _observations, ILogger _logger) : IRuleAutomationExecutor
{
    public async Task EvaluateAsync(InverterData? data, List<TriggerRule> dueRules, Guid? effectiveSource, DateTime now,
        Func<Task<bool>> isCurrent, string deviceKey, CancellationToken ct)
    {
        if (!await isCurrent()) return;
        _logger.LogInformation("Evaluating {Count} rule(s)", dueRules.Count);
        var evaluationContext = data is null ? new RuleEvaluationContext(null) : await _history.BuildContextAsync(now, data.InverterId?.ToString("D") ?? deviceKey, ct);

        var displayOpts = await _settingsService.LoadSectionAsync<DeyeSolar.Domain.Options.DisplayOptions>("Display");
        if (!await isCurrent()) return;
        var actions = new List<RuleAction>();
        var decisions = new Dictionary<int, RuleDecision>();
        var successfulActions = new HashSet<int>();
        var failedActions = new Dictionary<int, string>();
        var recordedRules = new HashSet<int>();
        foreach (var rule in dueRules)
        {
            try
            {
                var observed = await _observations.ReconcileAsync(rule, now, ct);
                // Unknown physical state cannot authorize ON. Time-window OFF may use the last acknowledged ON state.
                var decision = _ruleEvaluator.Decide(observed ? data : null, rule, DateTimeOffset.Now, displayOpts.TimeZoneId, evaluationContext);
                decisions[rule.Id] = decision;
                if (decision.Action is { } action) actions.Add(action);
            }
            catch (DeyeSolar.Web.Billing.BillingAccessException error)
            {
                await _ruleRepository.RecordEvaluationAsync(rule.Id, now, ct);
                failedActions[rule.Id] = error.Message;
                recordedRules.Add(rule.Id);
            }
        }

        foreach (var action in actions)
        {
            if (!await isCurrent()) break;
            var rule = dueRules.First(r => r.Id == action.RuleId);
            var dispatchDecision = _ruleEvaluator.Decide(data, rule, DateTimeOffset.Now, displayOpts.TimeZoneId, evaluationContext);
            if (dispatchDecision.TurnOn != action.TurnOn) continue;
            decisions[rule.Id] = dispatchDecision;
            try
            {
                using var sourceGuard = IntegrationAutomationSourceGuard.Enter(effectiveSource, rule, data, action.TurnOn,
                    () =>
                    {
                        var current = _ruleEvaluator.Decide(data, rule, DateTimeOffset.Now, displayOpts.TimeZoneId, evaluationContext);
                        if (current.TurnOn != action.TurnOn) return false;
                        decisions[rule.Id] = current;
                        return true;
                    });
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
            if (data is { BatterySocValid: not false } && !recordedRules.Contains(rule.Id) && !actions.Any(a => a.RuleId == rule.Id))
            {
                rule.LastEvaluated = now;
                await _ruleRepository.RecordEvaluationAsync(rule.Id, now, ct);
                recordedRules.Add(rule.Id);
            }
        }

        if (recordedRules.Count > 0)
            await _history.RecordAsync(data ?? new InverterData { BatterySocValid = false }, dueRules
                .Where(rule => rule.Enabled && recordedRules.Contains(rule.Id))
                .Select(rule => new RuleRunOutcome(rule.Name, decisions.GetValueOrDefault(rule.Id), successfulActions.Contains(rule.Id), failedActions.GetValueOrDefault(rule.Id),
                    rule.Id, RuleConfigurationVersion.Read(rule)))
                .ToArray(), ct);
    }

}
