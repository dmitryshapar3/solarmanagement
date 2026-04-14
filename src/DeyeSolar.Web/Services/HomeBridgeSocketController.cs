using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Infrastructure.Tuya;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Web.Services;

public class HomeBridgeSocketController : ISocketController
{
    private readonly BridgeStateService _bridgeState;
    private readonly IOptionsMonitor<SocketBackendOptions> _backendOptions;
    private readonly IOptionsMonitor<TuyaOptions> _tuyaOptions;

    public HomeBridgeSocketController(
        BridgeStateService bridgeState,
        IOptionsMonitor<SocketBackendOptions> backendOptions,
        IOptionsMonitor<TuyaOptions> tuyaOptions)
    {
        _bridgeState = bridgeState;
        _backendOptions = backendOptions;
        _tuyaOptions = tuyaOptions;
    }

    public Task TurnOnAsync(string entityId, CancellationToken ct)
        => SetStateAsync(entityId, true, ct);

    public Task TurnOffAsync(string entityId, CancellationToken ct)
        => SetStateAsync(entityId, false, ct);

    public async Task<bool> GetStateAsync(string entityId, CancellationToken ct)
    {
        var bridgeId = _backendOptions.CurrentValue.BridgeId;
        var deviceId = ResolveDeviceId(entityId);
        var staleCutoff = DateTime.UtcNow.AddSeconds(-Math.Max(1, _backendOptions.CurrentValue.StateStaleAfterSeconds));
        var device = await _bridgeState.GetDeviceShadowAsync(bridgeId, deviceId, ct);

        if (device == null)
            throw new InvalidOperationException($"No bridge state is available yet for device {deviceId}.");

        if (device.LastSeenAt < staleCutoff)
        {
            throw new InvalidOperationException(
                $"Bridge state for device {deviceId} is stale (last seen {device.LastSeenAt:O}).");
        }

        if (!device.Online)
            throw new InvalidOperationException($"Bridge reports device {deviceId} as offline.");

        return device.IsOn;
    }

    private async Task SetStateAsync(string entityId, bool desiredState, CancellationToken ct)
    {
        var bridgeId = _backendOptions.CurrentValue.BridgeId;
        var deviceId = ResolveDeviceId(entityId);
        var commandId = await _bridgeState.EnqueueSetStateCommandAsync(bridgeId, deviceId, desiredState, ct);
        var result = await _bridgeState.WaitForCommandCompletionAsync(
            commandId,
            TimeSpan.FromSeconds(Math.Max(1, _backendOptions.CurrentValue.CommandTimeoutSeconds)),
            ct);

        if (!string.Equals(result.Status, BridgeCommandStatuses.Succeeded, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                result.ResultMessage ?? $"Home bridge command failed with status '{result.Status}'.");
        }
    }

    private string ResolveDeviceId(string entityId)
    {
        if (!string.IsNullOrWhiteSpace(entityId))
            return entityId;

        var configuredDeviceId = _tuyaOptions.CurrentValue.DeviceId;
        if (!string.IsNullOrWhiteSpace(configuredDeviceId))
            return configuredDeviceId;

        throw new InvalidOperationException("DeviceId is not configured. Select a device in Settings.");
    }
}
