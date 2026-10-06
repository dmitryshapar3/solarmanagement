using DeyeSolar.Web.Data;
using System.Text.Json;
using SolarManagement.Integrations.Contracts;
using SolarManagement.SmartSockets.Contracts;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Integrations;

/// <summary>Reads and validates physical state independently of command intent and acknowledgement.</summary>
internal sealed class SocketObservationReader(IIntegrationRuntimeExecutor executor, SocketBindingSessionGuard bindings,
    ISocketAccessPolicy access, TimeProvider clock, IDbContextFactory<DeyeSolarDbContext>? history = null)
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
        if (history is not null)
        {
            var now = clock.GetUtcNow();
            var known = data.Online == true && data.IsOn.HasValue && data.ObservedAt is { } observed
                && now - observed <= TimeSpan.FromMinutes(10);
            var at = known ? data.ObservedAt!.Value.UtcDateTime : now.UtcDateTime;
            var state = known ? data.IsOn : null;
            var reason = known ? "provider_observation" : "state_unavailable";
            await using var db = await history.CreateDbContextAsync(ct);
            var id = binding.Id.ToString("D");
            if (!await db.ActivityEvents.AnyAsync(e => e.Kind == "device.observed" && e.DeviceId == id
                && e.OccurredAt == at && e.Generation == session.Generation && e.State == state, ct))
            {
                db.ActivityEvents.Add(new Redesign.ActivityEvent
                {
                    Kind = "device.observed", DeviceId = id, OccurredAt = at, RecordedAt = now.UtcDateTime,
                    State = state, Generation = session.Generation, ReasonCode = reason
                });
                await db.SaveChangesAsync(ct);
            }
        }
        return new(new(binding.Id), data.IsOn is null ? SwitchState.Unknown : data.IsOn.Value ? SwitchState.On : SwitchState.Off,
            data.Online is null ? Reachability.Unknown : data.Online.Value ? Reachability.Online : Reachability.Offline,
            data.CurrentPowerWatts is { } watts ? new Watts(watts) : null, data.ObservedAt, clock.GetUtcNow());
    }
}
