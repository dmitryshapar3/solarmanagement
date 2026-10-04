using DeyeSolar.Web.Data;
using System.Text.Json;
using SolarManagement.Integrations.Contracts;
using SolarManagement.SmartSockets.Contracts;

namespace DeyeSolar.Web.Integrations;

/// <summary>Reads and validates physical state independently of command intent and acknowledgement.</summary>
internal sealed class SocketObservationReader(IIntegrationRuntimeExecutor executor, SocketBindingSessionGuard bindings,
    ISocketAccessPolicy access, TimeProvider clock)
{
    public async Task<SocketState> ReadAsync(IntegrationDeviceBindingEntity binding, IntegrationSession expected, CancellationToken ct)
    {
        await access.EnsureAsync(ct);
        var session = await bindings.CurrentAsync(binding, expected, ct);
        var json = await executor.InvokeAsync(session, "socket.read", IntegrationJson.Element(new { remoteId = binding.RemoteId, channel = binding.Channel }), ct);
        await bindings.CurrentAsync(binding, expected, ct);
        var data = json.Deserialize<ProviderSocketTelemetry>(IntegrationJson.Options) ?? throw new InvalidDataException("Missing socket state.");
        await access.EnsureAsync(ct);
        if (data.RemoteId != binding.RemoteId || (data.Channel ?? "") != binding.Channel
            || data.CurrentPowerWatts < 0 || data.ObservedAt > clock.GetUtcNow())
            throw new InvalidDataException("The socket observation has invalid provenance or values.");
        return new(new(binding.Id), data.IsOn is null ? SwitchState.Unknown : data.IsOn.Value ? SwitchState.On : SwitchState.Off,
            data.Online is null ? Reachability.Unknown : data.Online.Value ? Reachability.Online : Reachability.Offline,
            data.CurrentPowerWatts is { } watts ? new Watts(watts) : null, data.ObservedAt, clock.GetUtcNow());
    }
}
