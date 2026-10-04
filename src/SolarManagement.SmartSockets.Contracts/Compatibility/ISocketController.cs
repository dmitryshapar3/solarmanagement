namespace DeyeSolar.Domain.Interfaces;

public interface ISocketController
{
    Task TurnOnAsync(string entityId, CancellationToken ct);
    Task TurnOffAsync(string entityId, CancellationToken ct);
    Task<bool> GetStateAsync(string entityId, CancellationToken ct);
}
