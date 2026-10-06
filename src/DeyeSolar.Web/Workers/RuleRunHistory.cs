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
internal sealed class RuleRunHistory(IDbContextFactory<DeyeSolarDbContext> _dbFactory, ILogger _logger, TimeProvider _clock) : IRuleRunHistory
{
    private const int HistoryRetentionDays = 31;
    public async Task RecordAsync(
        InverterData data,
        IReadOnlyList<RuleRunOutcome> outcomes,
        CancellationToken ct)
    {
        try
        {
            var now = _clock.GetUtcNow();
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            foreach (var outcome in outcomes)
            {
                var presentation = RuleRunPresentation.From(outcome);
                await UpsertRuleRunLogAsync(db, outcome.RuleName, presentation.Action, presentation.ConditionKey, presentation.Reason,
                    outcome.Decision?.BatterySoc, data, now.UtcDateTime, ct);
                var eventKind = outcome.ActionSucceeded ? "rule.switched" : outcome.Failure is not null ? "rule.failed" : "rule.checked";
                var ruleId = outcome.RuleId ?? outcome.Decision?.RuleId;
                var target = outcome.Decision?.EntityId;
                var reasonCode = outcome.Failure is not null ? "command_failed" : outcome.Decision?.Reason.ToString();
                var previous = ruleId is null ? null : await db.ActivityEvents.AsNoTracking()
                    .Where(e => e.RuleId == ruleId || e.DeviceId == target && e.Kind.StartsWith("command."))
                    .OrderByDescending(e => e.Id).FirstOrDefaultAsync(ct);
                var continues = eventKind == "rule.checked" && previous?.Kind == eventKind
                    && previous.ConfigurationVersion == outcome.ConfigurationVersion && previous.ReasonCode == reasonCode
                    && previous.DeviceId == target && previous.Generation == data.RuntimeGeneration
                    && now.UtcDateTime - previous.OccurredAt <= TimeSpan.FromMinutes(10);
                db.ActivityEvents.Add(new Redesign.ActivityEvent
                {
                    Kind = eventKind, GroupId = continues ? previous!.GroupId ?? previous.Id : null,
                    OccurredAt = now.UtcDateTime, RecordedAt = now.UtcDateTime,
                    RuleId = ruleId, RuleName = outcome.RuleName,
                    DeviceId = outcome.Decision?.EntityId, ConfigurationVersion = outcome.ConfigurationVersion,
                    State = outcome.ActionSucceeded ? outcome.Decision?.TurnOn : null,
                    ReasonCode = reasonCode, Generation = data.RuntimeGeneration,
                    BatterySoc = outcome.Decision?.BatterySoc, SolarWatts = outcome.Decision?.AverageSolarProductionWatts,
                    ValuesJson = outcome.Decision is null ? null : System.Text.Json.JsonSerializer.Serialize(outcome.Decision)
                });
            }

            await db.SaveChangesAsync(ct);
            await db.ActivityEvents.Where(e => e.OccurredAt < now.UtcDateTime.AddDays(-31)).ExecuteDeleteAsync(ct);

            // Retain the complete time range offered by browser and API history queries.
            var cutoff = HistoryQueryPolicy.Cutoff(now, HistoryQueryPolicy.MaximumHours);
            var old = db.RuleRunLogs.Where(r => r.Timestamp < cutoff);
            db.RuleRunLogs.RemoveRange(old);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to log rule runs");
        }
    }

    private static async Task UpsertRuleRunLogAsync(
        DeyeSolarDbContext db,
        string ruleName,
        string action,
        string conditionKey,
        string reason,
        int? batterySoc,
        InverterData data,
        DateTime recordedAt,
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
            latest.Timestamp = recordedAt;
            latest.Reason = reason;
            latest.BatterySoc = batterySoc;
            latest.SolarProduction = data.SolarPowerValid ? data.SolarProduction : null;
            latest.BatteryPower = data.BatteryPowerValid ? data.BatteryPower : null;
            return;
        }

        db.RuleRunLogs.Add(new RuleRunLog
        {
            Timestamp = recordedAt,
            RuleName = ruleName,
            Action = action,
            ConditionKey = conditionKey,
            Reason = reason,
            BatterySoc = batterySoc,
            SolarProduction = data.SolarPowerValid ? data.SolarProduction : null,
            BatteryPower = data.BatteryPowerValid ? data.BatteryPower : null
        });
    }

    private static string NormalizeConditionKey(string conditionKey)
        => conditionKey.Length <= 160 ? conditionKey : conditionKey[..160];

    public async Task<RuleEvaluationContext> BuildContextAsync(DateTime now, string deviceKey, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var cutoff = now.AddMinutes(-RuleEvaluator.SolarProductionAverageWindowMinutes);

        var averageSolar = await db.Readings
            .Where(r => r.Timestamp >= cutoff && r.SolarObservedAt >= cutoff && r.SolarPowerValid && r.BatterySocValid
                && r.SolarDeviceSn == deviceKey &&
                r.BatterySoc < RuleEvaluator.SolarProductionBypassSocThreshold)
            .AverageAsync(r => (double?)r.SolarProduction, ct);

        return new RuleEvaluationContext(
            averageSolar.HasValue ? (int)Math.Round(averageSolar.Value) : null);
    }

    public async Task CleanupAsync(CancellationToken ct)
    {
        try
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            await ExpiredReadings(db.Readings, now).ExecuteDeleteAsync(ct);
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
