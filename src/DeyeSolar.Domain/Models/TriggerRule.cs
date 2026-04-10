namespace DeyeSolar.Domain.Models;

public class TriggerRule
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    // Turn ON when battery SOC is at or above this percentage
    public int SocTurnOnThreshold { get; set; } = 80;

    // Hard safety floor: force OFF if SOC drops to this percentage (overrides MinOnMinutes)
    public int SocFloor { get; set; } = 55;

    // Turn OFF when net battery drain over the window reaches this many Wh
    public int MaxDrainWh { get; set; } = 200;

    // Window over which net battery drain is accumulated (minutes)
    public int DrainWindowMinutes { get; set; } = 15;

    // After turning ON, keep ON for at least this many minutes (unless SocFloor hit)
    public int MinOnMinutes { get; set; } = 10;

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
