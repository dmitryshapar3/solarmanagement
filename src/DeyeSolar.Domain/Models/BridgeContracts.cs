namespace DeyeSolar.Domain.Models;

public static class BridgeCommandTypes
{
    public const string SetState = "set_state";
    public const string RefreshInventory = "refresh_inventory";
}

public static class BridgeCommandStatuses
{
    public const string Queued = "queued";
    public const string Leased = "leased";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

public record BridgeHeartbeatInfo(
    string BridgeId,
    DateTimeOffset LastSeenAt,
    string? BridgeVersion,
    string? HostName);

public class BridgeSyncRequest
{
    public string BridgeId { get; set; } = string.Empty;
    public BridgeHeartbeatReport Heartbeat { get; set; } = new();
    public List<BridgeDeviceReport> Devices { get; set; } = [];
    public List<BridgeCommandResultReport> CompletedCommands { get; set; } = [];
}

public class BridgeSyncResponse
{
    public List<BridgePendingCommand> PendingCommands { get; set; } = [];
}

public class BridgeHeartbeatReport
{
    public string? BridgeVersion { get; set; }
    public string? HostName { get; set; }
    public DateTimeOffset? ObservedAt { get; set; }
}

public class BridgeDeviceReport
{
    public string DeviceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Category { get; set; }
    public bool Online { get; set; }
    public bool IsOn { get; set; }
    public int? CurrentPowerW { get; set; }
    public string? Error { get; set; }
}

public class BridgeCommandResultReport
{
    public Guid CommandId { get; set; }
    public bool Success { get; set; }
    public string? Message { get; set; }
}

public class BridgePendingCommand
{
    public Guid CommandId { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string CommandType { get; set; } = string.Empty;
    public bool? DesiredState { get; set; }
}
