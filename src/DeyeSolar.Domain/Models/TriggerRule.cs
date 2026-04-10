namespace DeyeSolar.Domain.Models;

public class TriggerRule
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int? SocTurnOnThreshold { get; set; }
    public int? MaxConsumptionWh { get; set; }
    public int MonitoringWindowMinutes { get; set; } = 15;
    public int CooldownMinutes { get; set; } = 15;
    public TimeOnly? ActiveFrom { get; set; }
    public TimeOnly? ActiveTo { get; set; }
    public int IntervalSeconds { get; set; } = 30;
    public bool CurrentState { get; set; }
    public DateTime? CurrentStateChangedAt { get; set; }
    public DateTime? LastEvaluated { get; set; }
    public DateTime? LastTurnedOff { get; set; }
}
