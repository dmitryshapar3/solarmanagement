using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Redesign;

/// <summary>Append-only evidence. Unknown historical observations are never backfilled.</summary>
public sealed class ActivityEvent : IInstallationOwned
{
    public long Id { get; set; }
    public long? GroupId { get; set; }
    public string InstallationId { get; set; } = "";
    public DateTime OccurredAt { get; set; }
    public DateTime RecordedAt { get; set; }
    public string Kind { get; set; } = "";
    public int? RuleId { get; set; }
    public string? DeviceId { get; set; }
    public string? RuleName { get; set; }
    public string? ConfigurationVersion { get; set; }
    public string? ReasonCode { get; set; }
    public string? ActorUserId { get; set; }
    public string? Client { get; set; }
    public bool? State { get; set; }
    public int? BatterySoc { get; set; }
    public int? SolarWatts { get; set; }
    public long? Generation { get; set; }
    public string? ValuesJson { get; set; }
}

public static class ActivityEvidence
{
    public static ActivityEvent RuleChange(TriggerRule rule, string kind, string? actor = null) => new()
    {
        OccurredAt = DateTime.UtcNow, RecordedAt = DateTime.UtcNow, Kind = kind,
        RuleId = rule.Id, DeviceId = rule.EntityId, RuleName = rule.Name,
        ConfigurationVersion = RuleConfigurationVersion.Read(rule), ActorUserId = actor
    };
}
