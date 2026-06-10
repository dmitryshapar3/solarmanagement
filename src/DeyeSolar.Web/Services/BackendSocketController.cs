using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Infrastructure.Shelly;

namespace DeyeSolar.Web.Services;

public class BackendSocketController : ISocketController
{
    private readonly ShellyCloudClient _shellyCloudClient;

    public BackendSocketController(ShellyCloudClient shellyCloudClient)
    {
        _shellyCloudClient = shellyCloudClient;
    }

    public Task TurnOnAsync(string entityId, CancellationToken ct)
        => _shellyCloudClient.TurnOnAsync(SocketEntityIds.RawIdOrSelf(entityId), ct);

    public Task TurnOffAsync(string entityId, CancellationToken ct)
        => _shellyCloudClient.TurnOffAsync(SocketEntityIds.RawIdOrSelf(entityId), ct);

    public Task<bool> GetStateAsync(string entityId, CancellationToken ct)
        => _shellyCloudClient.GetStateAsync(SocketEntityIds.RawIdOrSelf(entityId), ct);
}
