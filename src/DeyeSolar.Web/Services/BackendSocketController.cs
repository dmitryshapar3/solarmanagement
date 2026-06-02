using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Infrastructure.Shelly;
using DeyeSolar.Infrastructure.Tuya;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Web.Services;

public class BackendSocketController : ISocketController
{
    private readonly IOptionsMonitor<SocketBackendOptions> _backendOptions;
    private readonly TuyaCloudClient _tuyaCloudClient;
    private readonly ShellyCloudClient _shellyCloudClient;

    public BackendSocketController(
        IOptionsMonitor<SocketBackendOptions> backendOptions,
        TuyaCloudClient tuyaCloudClient,
        ShellyCloudClient shellyCloudClient)
    {
        _backendOptions = backendOptions;
        _tuyaCloudClient = tuyaCloudClient;
        _shellyCloudClient = shellyCloudClient;
    }

    public Task TurnOnAsync(string entityId, CancellationToken ct)
    {
        var controller = ResolveController(entityId, out var rawEntityId);
        return controller.TurnOnAsync(rawEntityId, ct);
    }

    public Task TurnOffAsync(string entityId, CancellationToken ct)
    {
        var controller = ResolveController(entityId, out var rawEntityId);
        return controller.TurnOffAsync(rawEntityId, ct);
    }

    public Task<bool> GetStateAsync(string entityId, CancellationToken ct)
    {
        var controller = ResolveController(entityId, out var rawEntityId);
        return controller.GetStateAsync(rawEntityId, ct);
    }

    private ISocketController ResolveController(string entityId, out string rawEntityId)
    {
        if (SocketEntityIds.TryParse(entityId, out var source, out rawEntityId))
        {
            return source switch
            {
                SocketDeviceSources.Shelly => _shellyCloudClient,
                SocketDeviceSources.Tuya => _tuyaCloudClient,
                _ => _tuyaCloudClient
            };
        }

        rawEntityId = entityId;
        var mode = _backendOptions.CurrentValue.Mode;
        if (SocketBackendModes.IsCloudShelly(mode))
            return _shellyCloudClient;
        return _tuyaCloudClient;
    }
}
