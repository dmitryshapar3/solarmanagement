using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Options;
using DeyeSolar.Infrastructure.Tuya;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Web.Services;

public class BackendSocketController : ISocketController
{
    private readonly IOptionsMonitor<SocketBackendOptions> _backendOptions;
    private readonly TuyaCloudClient _tuyaCloudClient;
    private readonly HomeBridgeSocketController _homeBridgeSocketController;

    public BackendSocketController(
        IOptionsMonitor<SocketBackendOptions> backendOptions,
        TuyaCloudClient tuyaCloudClient,
        HomeBridgeSocketController homeBridgeSocketController)
    {
        _backendOptions = backendOptions;
        _tuyaCloudClient = tuyaCloudClient;
        _homeBridgeSocketController = homeBridgeSocketController;
    }

    public Task TurnOnAsync(string entityId, CancellationToken ct)
        => ResolveController().TurnOnAsync(entityId, ct);

    public Task TurnOffAsync(string entityId, CancellationToken ct)
        => ResolveController().TurnOffAsync(entityId, ct);

    public Task<bool> GetStateAsync(string entityId, CancellationToken ct)
        => ResolveController().GetStateAsync(entityId, ct);

    private ISocketController ResolveController()
        => SocketBackendModes.IsHomeBridge(_backendOptions.CurrentValue.Mode)
            ? _homeBridgeSocketController
            : _tuyaCloudClient;
}
