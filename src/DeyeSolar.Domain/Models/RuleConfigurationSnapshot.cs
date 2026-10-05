using System.Text.Json.Serialization;

namespace DeyeSolar.Domain.Models;

/// <summary>Configuration ownership is distinct from command observations and evaluation history.</summary>
public sealed record RuleConfigurationSnapshot(
    int Id, string InstallationId, string Name, string EntityId, Guid? SourceInverterId, bool Enabled,
    int SocTurnOnThreshold, bool UseSeparateSocTurnOffThreshold, int SocTurnOffThreshold,
    bool UseSolarProductionThreshold, int MinAverageSolarProductionWatts, int CooldownMinutes,
    int IntervalSeconds, TimeOnly? ActiveFrom, TimeOnly? ActiveTo)
{
    public static RuleConfigurationSnapshot From(TriggerRule rule) => new(
        rule.Id, rule.InstallationId, rule.Name, rule.EntityId, rule.SourceInverterId, rule.Enabled,
        rule.SocTurnOnThreshold, rule.UseSeparateSocTurnOffThreshold, rule.SocTurnOffThreshold,
        rule.UseSolarProductionThreshold, rule.MinAverageSolarProductionWatts, rule.CooldownMinutes,
        rule.IntervalSeconds, rule.ActiveFrom, rule.ActiveTo);

    // Renaming a rule does not change the physical decision already evaluated for it.
    [JsonIgnore]
    public RuleExecutionConfiguration Execution => new(EntityId, SourceInverterId, Enabled,
        SocTurnOnThreshold, UseSeparateSocTurnOffThreshold, SocTurnOffThreshold,
        UseSolarProductionThreshold, MinAverageSolarProductionWatts, CooldownMinutes,
        IntervalSeconds, ActiveFrom, ActiveTo);

    public void ApplyTo(TriggerRule rule)
    {
        rule.Name = Name;
        rule.EntityId = EntityId;
        rule.SourceInverterId = SourceInverterId;
        rule.Enabled = Enabled;
        rule.SocTurnOnThreshold = SocTurnOnThreshold;
        rule.UseSeparateSocTurnOffThreshold = UseSeparateSocTurnOffThreshold;
        rule.SocTurnOffThreshold = SocTurnOffThreshold;
        rule.UseSolarProductionThreshold = UseSolarProductionThreshold;
        rule.MinAverageSolarProductionWatts = MinAverageSolarProductionWatts;
        rule.CooldownMinutes = CooldownMinutes;
        rule.IntervalSeconds = IntervalSeconds;
        rule.ActiveFrom = ActiveFrom;
        rule.ActiveTo = ActiveTo;
    }
}

public sealed record RuleExecutionConfiguration(string EntityId, Guid? SourceInverterId, bool Enabled,
    int SocTurnOnThreshold, bool UseSeparateSocTurnOffThreshold, int SocTurnOffThreshold,
    bool UseSolarProductionThreshold, int MinAverageSolarProductionWatts, int CooldownMinutes,
    int IntervalSeconds, TimeOnly? ActiveFrom, TimeOnly? ActiveTo);
