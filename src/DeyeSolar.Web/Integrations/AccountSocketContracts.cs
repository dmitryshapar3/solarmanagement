using SolarManagement.SmartSockets.Contracts;

namespace DeyeSolar.Web.Integrations;

public interface IAccountSocketCatalog
{
    Task<ISmartSocket> GetForUserAsync(SocketId id, string userId, Func<CancellationToken, Task> authorizeCommand, CancellationToken ct);
}

public interface IAccountSocketControl
{
    Task SetPowerForUserAsync(string entityId, SwitchState desiredState, string userId, Func<CancellationToken, Task> authorizeCommand, CancellationToken ct);
    Task<SocketCommandReceipt> ReleaseForUserAsync(SocketId deviceId, SocketCommandId commandId, string userId,
        Func<CancellationToken, Task> authorizeCommand, CancellationToken ct);
}
