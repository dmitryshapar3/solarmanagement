using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Infrastructure.Tuya;

namespace DeyeSolar.Web.Services;

public class CloudSocketInventoryService : ISocketInventoryService
{
    private readonly TuyaCloudClient _tuyaClient;

    public CloudSocketInventoryService(TuyaCloudClient tuyaClient)
    {
        _tuyaClient = tuyaClient;
    }

    public async Task<IReadOnlyList<DevicePowerInfo>> GetCachedDevicesAsync(CancellationToken ct)
        => await _tuyaClient.GetDevicesWithStatusAsync(ct);

    public Task<IReadOnlyList<DevicePowerInfo>> RefreshDevicesAsync(CancellationToken ct)
        => GetCachedDevicesAsync(ct);

    public Task<BridgeHeartbeatInfo?> GetBridgeHeartbeatAsync(CancellationToken ct)
        => Task.FromResult<BridgeHeartbeatInfo?>(null);
}
