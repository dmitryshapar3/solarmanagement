namespace DeyeSolar.Domain.Models;

public class TriggerRule
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int? SocTurnOnThreshold { get; set; }
    public int? SocTurnOffThreshold { get; set; }
    public int? MinSolarSurplusWatts { get; set; }
    public TimeOnly? ActiveFrom { get; set; }
    public TimeOnly? ActiveTo { get; set; }
    public int CooldownSeconds { get; set; } = 60;
    public bool CurrentState { get; set; }
    public DateTimeOffset? LastTriggered { get; set; }
}
