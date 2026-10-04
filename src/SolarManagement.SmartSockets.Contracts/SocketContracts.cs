namespace SolarManagement.SmartSockets.Contracts;

public readonly record struct SocketId(Guid Value);
public readonly record struct Watts(int Value);
public readonly record struct SocketCommandId(Guid Value);
public enum SwitchState { Unknown, Off, On }
public enum Reachability { Unknown, Offline, Online }
public enum SocketCommandStatus { Pending, Acknowledged, Rejected, Uncertain, UncertainClosed }
public enum SocketCommandRejection { Unsupported, Offline, ConfigurationChanged, DeviceUnavailable }
public enum SocketInventoryIssueKind { Unavailable, AuthenticationRequired }
public sealed record SocketCapabilities(bool CanSwitch, bool CanMeasurePower);
public sealed record SocketDescriptor(SocketId Id, string Name,
    SocketCapabilities Capabilities, Reachability Reachability);
public sealed record SocketInventoryIssue(string DisplaySource, SocketInventoryIssueKind Kind);
public sealed record SocketInventorySnapshot(IReadOnlyList<SocketDescriptor> Devices,
    IReadOnlyList<SocketInventoryIssue> Issues, DateTimeOffset RefreshedAt);
public sealed record SocketState(SocketId DeviceId, SwitchState Power, Reachability Reachability,
    Watts? CurrentPower, DateTimeOffset? ObservedAt, DateTimeOffset ReceivedAt);
public sealed record SetSocketPowerCommand(SocketCommandId CommandId, SwitchState DesiredState);
public sealed record SocketCommandResult(SocketCommandId CommandId, SocketCommandStatus Status,
    SocketCommandRejection? Rejection, SocketState? ObservedState);
public sealed record SocketCommandReceipt(SocketId DeviceId, SocketCommandId CommandId, SwitchState DesiredState,
    SocketCommandStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

public interface ISmartSocket
{
    SocketId Id { get; }
    SocketCapabilities Capabilities { get; }
    Task<SocketState> ReadAsync(CancellationToken ct);
    Task<SocketCommandResult> SetPowerAsync(SetSocketPowerCommand command, CancellationToken ct);
}
public interface ISmartSocketCatalog
{
    Task<SocketInventorySnapshot> ReadInventoryAsync(bool forceRefresh, CancellationToken ct);
    Task<ISmartSocket> GetAsync(SocketId id, CancellationToken ct);
}
public interface ISocketCommandTracker
{
    Task<SocketCommandResult> ReadResultAsync(SocketId deviceId,
        SocketCommandId commandId, CancellationToken ct);
    Task<IReadOnlyList<SocketCommandReceipt>> ListUnresolvedAsync(SocketId deviceId, CancellationToken ct);
    Task<SocketCommandReceipt> ReleaseAsync(SocketId deviceId, SocketCommandId commandId, CancellationToken ct);
}
