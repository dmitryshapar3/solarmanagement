using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Services;

namespace DeyeSolar.Web.Api;

public class MobileSocketCommandService
{
    private readonly ISocketController _socketController;
    private readonly IRuleRepository _rules;
    private readonly DeviceStatusSnapshot _devices;

    public MobileSocketCommandService(
        ISocketController socketController,
        IRuleRepository rules,
        DeviceStatusSnapshot devices)
    {
        _socketController = socketController;
        _rules = rules;
        _devices = devices;
    }

    public async Task<SocketStateResponse> SetStateAsync(string entityId, bool isOn, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entityId))
            throw new InvalidOperationException("EntityId is required.");

        if (isOn)
            await _socketController.TurnOnAsync(entityId, ct);
        else
            await _socketController.TurnOffAsync(entityId, ct);

        await UpdateMatchingRuleStatesAsync(entityId, isOn, ct);
        _devices.SetDeviceState(entityId, isOn);

        var device = _devices.Current?
            .FirstOrDefault(d => string.Equals(d.Id, entityId, StringComparison.OrdinalIgnoreCase))
            ?.ToDto();

        return new SocketStateResponse(entityId, isOn, device);
    }

    private async Task UpdateMatchingRuleStatesAsync(string entityId, bool isOn, CancellationToken ct)
    {
        var rules = await _rules.GetAllAsync(ct);
        foreach (var rule in rules.Where(r => string.Equals(r.EntityId, entityId, StringComparison.OrdinalIgnoreCase)))
        {
            if (rule.CurrentState == isOn)
                continue;

            rule.CurrentState = isOn;
            await _rules.UpdateAsync(rule, ct);
        }
    }
}
