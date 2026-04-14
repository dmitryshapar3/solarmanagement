using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Web.Services;

public class HomeBridgeInventoryService : ISocketInventoryService
{
    private readonly BridgeStateService _bridgeState;
    private readonly IOptionsMonitor<SocketBackendOptions> _backendOptions;

    public HomeBridgeInventoryService(
        BridgeStateService bridgeState,
        IOptionsMonitor<SocketBackendOptions> backendOptions)
    {
        _bridgeState = bridgeState;
        _backendOptions = backendOptions;
    }

    public async Task<IReadOnlyList<DevicePowerInfo>> GetCachedDevicesAsync(CancellationToken ct)
    {
        var options = _backendOptions.CurrentValue;
        var devices = await _bridgeState.GetDeviceShadowsAsync(options.BridgeId, ct);
        var staleCutoff = DateTime.UtcNow.AddSeconds(-Math.Max(1, options.StateStaleAfterSeconds));

        return devices.Select(d =>
        {
            var isFresh = d.LastSeenAt >= staleCutoff;
            return new DevicePowerInfo(
                d.DeviceId,
                d.Name,
                d.Category,
                d.Online && isFresh,
                d.IsOn,
                isFresh ? d.CurrentPowerW : null);
        }).ToList();
    }

    public async Task<IReadOnlyList<DevicePowerInfo>> RefreshDevicesAsync(CancellationToken ct)
    {
        var options = _backendOptions.CurrentValue;
        var commandId = await _bridgeState.EnqueueRefreshInventoryCommandAsync(options.BridgeId, ct);
        var result = await _bridgeState.WaitForCommandCompletionAsync(
            commandId,
            TimeSpan.FromSeconds(Math.Max(1, options.CommandTimeoutSeconds)),
            ct);

        if (!string.Equals(result.Status, BridgeCommandStatuses.Succeeded, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                result.ResultMessage ?? $"Home bridge inventory refresh failed with status '{result.Status}'.");
        }

        return await GetCachedDevicesAsync(ct);
    }

    public Task<BridgeHeartbeatInfo?> GetBridgeHeartbeatAsync(CancellationToken ct)
        => _bridgeState.GetHeartbeatAsync(_backendOptions.CurrentValue.BridgeId, ct);
}
