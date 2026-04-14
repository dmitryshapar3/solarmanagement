using DeyeSolar.Domain.Models;

namespace DeyeSolar.Domain.Interfaces;

public interface ISocketInventoryService
{
    Task<IReadOnlyList<DevicePowerInfo>> GetCachedDevicesAsync(CancellationToken ct);
    Task<IReadOnlyList<DevicePowerInfo>> RefreshDevicesAsync(CancellationToken ct);
    Task<BridgeHeartbeatInfo?> GetBridgeHeartbeatAsync(CancellationToken ct);
}
