namespace DeyeSolar.Web.Data;

public class BridgeDeviceShadow
{
    public string BridgeId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Category { get; set; }
    public bool Online { get; set; }
    public bool IsOn { get; set; }
    public int? CurrentPowerW { get; set; }
    public DateTime LastSeenAt { get; set; }
    public string? Error { get; set; }
}

public class BridgeCommand
{
    public Guid Id { get; set; }
    public string BridgeId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string CommandType { get; set; } = string.Empty;
    public bool? DesiredState { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }
    public DateTime? LeasedAt { get; set; }
    public DateTime? LeaseExpiresAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ResultMessage { get; set; }
}

public class BridgeHeartbeat
{
    public string BridgeId { get; set; } = string.Empty;
    public DateTime LastSeenAt { get; set; }
    public string? BridgeVersion { get; set; }
    public string? HostName { get; set; }
}
