using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Infrastructure.Shelly;

namespace DeyeSolar.Web.Services;

public class ShellySocketInventoryService : ISocketInventoryService
{
    private readonly ShellyCloudClient _shellyClient;

    public ShellySocketInventoryService(ShellyCloudClient shellyClient)
    {
        _shellyClient = shellyClient;
    }

    public async Task<IReadOnlyList<DevicePowerInfo>> GetCachedDevicesAsync(CancellationToken ct)
        => await _shellyClient.GetDevicesWithStatusAsync(ct);

    public Task<IReadOnlyList<DevicePowerInfo>> RefreshDevicesAsync(CancellationToken ct)
        => GetCachedDevicesAsync(ct);
}
