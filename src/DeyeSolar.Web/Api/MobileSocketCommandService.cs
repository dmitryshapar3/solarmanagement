using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Services;
using DeyeSolar.Web.Services;

namespace DeyeSolar.Web.Api;

public class MobileSocketCommandService
{
    private readonly ISocketController _socketController;
    private readonly IRuleRepository _rules;
    private readonly DeviceStatusSnapshot _devices;
    private readonly DeviceNameService? _names;

    public MobileSocketCommandService(
        ISocketController socketController,
        IRuleRepository rules,
        DeviceStatusSnapshot devices, DeviceNameService? names = null)
    {
        _socketController = socketController;
        _rules = rules;
        _devices = devices;
        _names = names;
    }

    public async Task<SocketStateResponse> SetStateAsync(string entityId, bool isOn, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entityId) || entityId.Length > 128)
            throw new InvalidOperationException("EntityId is required.");
        var selected = _devices.Current?.FirstOrDefault(device => DeviceNameService.CanonicalId(device.Id) == DeviceNameService.CanonicalId(entityId));
        if (selected is null || !selected.Online)
            throw new InvalidOperationException("Refresh devices and choose an online socket from this installation.");
        entityId = selected.Id;

        if (isOn)
            await _socketController.TurnOnAsync(entityId, ct);
        else
            await _socketController.TurnOffAsync(entityId, ct);

        await UpdateMatchingRuleStatesAsync(entityId, isOn, ct);
        _devices.SetDeviceState(entityId, isOn);

        var current = _devices.Current?.FirstOrDefault(d => DeviceNameService.CanonicalId(d.Id) == DeviceNameService.CanonicalId(entityId));
        var device = current is null ? null : _names is null ? current.ToDto() : (await _names.DescribeAsync([current], ct)).Single();

        return new SocketStateResponse(entityId, isOn, device);
    }

    private async Task UpdateMatchingRuleStatesAsync(string entityId, bool isOn, CancellationToken ct)
    {
        var rules = await _rules.GetAllAsync(ct);
        foreach (var rule in rules.Where(r => DeviceNameService.CanonicalId(r.EntityId) == DeviceNameService.CanonicalId(entityId)))
        {
            if (rule.CurrentState == isOn)
                continue;

            rule.CurrentState = isOn;
            await _rules.UpdateAsync(rule, ct);
        }
    }
}
