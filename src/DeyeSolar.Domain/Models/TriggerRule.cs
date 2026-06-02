namespace DeyeSolar.Domain.Models;

public class TriggerRule
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    // Turn ON when battery SOC is at or above this percentage
    public int SocTurnOnThreshold { get; set; } = 80;

    // When disabled, the turn-off threshold is the same as SocTurnOnThreshold
    public bool UseSeparateSocTurnOffThreshold { get; set; }

    // Turn OFF when battery SOC is at or below this percentage
    public int SocTurnOffThreshold { get; set; } = 80;

    // After turning OFF, keep OFF for at least this many minutes before re-evaluating turn-on
    public int CooldownMinutes { get; set; } = 15;

    // How often the rule is evaluated
    public int IntervalSeconds { get; set; } = 30;

    // Optional time-of-day window (local time, according to Display timezone)
    public TimeOnly? ActiveFrom { get; set; }
    public TimeOnly? ActiveTo { get; set; }

    // Runtime state
    public bool CurrentState { get; set; }
    public DateTime? CurrentStateChangedAt { get; set; }
    public DateTime? LastEvaluated { get; set; }
}
