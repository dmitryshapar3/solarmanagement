using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Integrations;
using System.Data;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Data;

public class RuleRepository : IRuleRepository
{
    private readonly IDbContextFactory<DeyeSolarDbContext> _dbFactory;

    public RuleRepository(IDbContextFactory<DeyeSolarDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<List<TriggerRule>> GetAllAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rules = await db.TriggerRules.ToListAsync(ct);
        foreach (var rule in rules) rule.ConfigurationVersion = RuleConfigurationVersion.Read(rule);
        return rules;
    }

    public async Task<TriggerRule?> GetByIdAsync(int id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rule = await db.TriggerRules.SingleOrDefaultAsync(rule => rule.Id == id, ct);
        if (rule is not null) rule.ConfigurationVersion = RuleConfigurationVersion.Read(rule);
        return rule;
    }

    public async Task<TriggerRule> CreateAsync(TriggerRule rule, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await RuleTargetPolicy.LockInstallationAsync(db, ct);
        await RuleTargetPolicy.ValidateAsync(db, rule, ct);
        await ValidateSourceAsync(db, rule, ct);
        db.TriggerRules.Add(rule);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        rule.ConfigurationVersion = RuleConfigurationVersion.Read(rule);
        return rule;
    }

    public async Task UpdateAsync(TriggerRule rule, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await RuleTargetPolicy.LockInstallationAsync(db, ct);
        var current = await db.TriggerRules.SingleOrDefaultAsync(existing => existing.Id == rule.Id, ct)
            ?? throw new ArgumentException("Rule not found.");
        RuleConfigurationVersion.Check(rule, current);
        await RuleTargetPolicy.ValidateAsync(db, rule, ct);
        await ValidateSourceAsync(db, rule, ct);
        // Configuration snapshots can predate an acknowledged command or another evaluation.
        // Only the command coordinator owns runtime state; an editor cannot restore its stale copy.
        if (await RuleTargetPolicy.IdentityAsync(db, current.EntityId, ct) != await RuleTargetPolicy.IdentityAsync(db, rule.EntityId, ct))
        {
            // Runtime history belongs to the physical target; observations initialize a replacement.
            current.CurrentState = false;
            current.CurrentStateChangedAt = null;
            current.LastEvaluated = null;
        }
        current.Name = rule.Name;
        current.EntityId = rule.EntityId;
        current.SourceInverterId = rule.SourceInverterId;
        current.Enabled = rule.Enabled;
        current.SocTurnOnThreshold = rule.SocTurnOnThreshold;
        current.UseSeparateSocTurnOffThreshold = rule.UseSeparateSocTurnOffThreshold;
        current.SocTurnOffThreshold = rule.SocTurnOffThreshold;
        current.UseSolarProductionThreshold = rule.UseSolarProductionThreshold;
        current.MinAverageSolarProductionWatts = rule.MinAverageSolarProductionWatts;
        current.CooldownMinutes = rule.CooldownMinutes;
        current.IntervalSeconds = rule.IntervalSeconds;
        current.ActiveFrom = rule.ActiveFrom;
        current.ActiveTo = rule.ActiveTo;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        rule.CurrentState = current.CurrentState;
        rule.CurrentStateChangedAt = current.CurrentStateChangedAt;
        rule.LastEvaluated = current.LastEvaluated;
        rule.ConfigurationVersion = RuleConfigurationVersion.Read(current);
    }

    public async Task RecordEvaluationAsync(int ruleId, DateTime when, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.TriggerRules.Where(rule => rule.Id == ruleId && (!rule.LastEvaluated.HasValue || rule.LastEvaluated < when))
            .ExecuteUpdateAsync(update => update.SetProperty(rule => rule.LastEvaluated, when), ct);
    }

    private static async Task ValidateSourceAsync(DeyeSolarDbContext db, TriggerRule rule, CancellationToken ct)
    {
        // Disabled drafts retain retired selections so an outage never prevents stopping a rule.
        if (!rule.Enabled) return;
        var effective = rule.SourceInverterId ?? (await IntegrationSocketAssociation.ResolveSourcesAsync(db, [rule], ct)).GetValueOrDefault(rule.Id);
        if (effective is not { } sourceId) return;
        var binding = await (from device in db.IntegrationDeviceBindings.AsNoTracking()
            join instance in db.IntegrationInstances.AsNoTracking() on device.InstanceId equals instance.Id
            where device.Id == sourceId && device.Enabled && device.Kind == "inverter" && instance.State == "enabled"
            select device).SingleOrDefaultAsync(ct);
        if (binding is null) throw new ArgumentException("Select an enabled inverter from this installation.");
        var capabilities = IntegrationCapabilities.Read(binding);
        if (!capabilities.HasBattery) throw new ArgumentException("The selected inverter does not provide battery SOC.");
        if (rule.UseSolarProductionThreshold && !capabilities.HasSolarPower)
            throw new ArgumentException("The selected inverter does not provide solar power.");
    }

    public async Task DeleteAsync(int id, string configurationVersion, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await RuleTargetPolicy.LockInstallationAsync(db, ct);
        RuleConfigurationVersion.Require(configurationVersion);
        var rule = await db.TriggerRules.SingleOrDefaultAsync(existing => existing.Id == id, ct);
        if (rule != null)
        {
            RuleConfigurationVersion.Check(configurationVersion, rule);
            db.TriggerRules.Remove(rule);
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }
}
