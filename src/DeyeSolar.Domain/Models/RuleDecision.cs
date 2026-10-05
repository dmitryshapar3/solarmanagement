namespace DeyeSolar.Domain.Models;

public enum RuleDecisionReason
{
    Disabled, OutsideActiveWindow, MeasurementUnavailable, SocBelowTurnOnThreshold,
    SocReachedTurnOffThreshold, RemainOn, Cooldown, SolarAverageUnavailable,
    SolarAverageBelowThreshold, TurnOnConditionsSatisfied
}

/// <summary>An immutable policy result, including the values needed to explain it without evaluating again.</summary>
public sealed record RuleDecision(int RuleId, string EntityId, DateTimeOffset EvaluatedAt, bool? TurnOn,
    RuleDecisionReason Reason, int? BatterySoc, int SocTurnOnThreshold, int SocTurnOffThreshold,
    double RemainingCooldownMinutes, bool UsesSolarThreshold, bool SolarThresholdBypassed,
    int? AverageSolarProductionWatts, int MinimumSolarProductionWatts)
{
    public RuleAction? Action => TurnOn is { } on ? new(RuleId, EntityId, on) : null;
}
