using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Web.Services;

public class BackendSocketInventoryService : ISocketInventoryService
{
    private readonly IOptionsMonitor<SocketBackendOptions> _backendOptions;
    private readonly CloudSocketInventoryService _cloudInventoryService;
    private readonly HomeBridgeInventoryService _homeBridgeInventoryService;

    public BackendSocketInventoryService(
        IOptionsMonitor<SocketBackendOptions> backendOptions,
        CloudSocketInventoryService cloudInventoryService,
        HomeBridgeInventoryService homeBridgeInventoryService)
    {
        _backendOptions = backendOptions;
        _cloudInventoryService = cloudInventoryService;
        _homeBridgeInventoryService = homeBridgeInventoryService;
    }

    public Task<IReadOnlyList<DevicePowerInfo>> GetCachedDevicesAsync(CancellationToken ct)
        => ResolveService().GetCachedDevicesAsync(ct);

    public Task<IReadOnlyList<DevicePowerInfo>> RefreshDevicesAsync(CancellationToken ct)
        => ResolveService().RefreshDevicesAsync(ct);

    public Task<BridgeHeartbeatInfo?> GetBridgeHeartbeatAsync(CancellationToken ct)
        => _homeBridgeInventoryService.GetBridgeHeartbeatAsync(ct);

    private ISocketInventoryService ResolveService()
        => SocketBackendModes.IsHomeBridge(_backendOptions.CurrentValue.Mode)
            ? _homeBridgeInventoryService
            : _cloudInventoryService;
}
