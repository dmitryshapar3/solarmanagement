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
            }

            await db.SaveChangesAsync(ct);

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
