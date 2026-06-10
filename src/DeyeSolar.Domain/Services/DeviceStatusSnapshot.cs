using DeyeSolar.Domain.Models;

namespace DeyeSolar.Domain.Services;

public class DeviceStatusSnapshot
{
    private IReadOnlyList<DevicePowerInfo>? _current;
    private DateTimeOffset? _lastUpdated;

    public IReadOnlyList<DevicePowerInfo>? Current => _current;

    public DateTimeOffset? LastUpdated => _lastUpdated;

    public event Action? OnDataUpdated;

    public void Update(IReadOnlyList<DevicePowerInfo> devices)
    {
        _current = devices;
        _lastUpdated = DateTimeOffset.UtcNow;
        OnDataUpdated?.Invoke();
    }

    public void SetDeviceState(string entityId, bool isOn)
    {
        if (_current == null)
            return;

        var updated = false;
        var devices = _current
            .Select(device =>
            {
                if (!string.Equals(device.Id, entityId, StringComparison.OrdinalIgnoreCase))
                    return device;

                updated = true;
                return device with { Online = true, IsOn = isOn };
            })
            .ToList();

        if (!updated)
            return;

        _current = devices;
        _lastUpdated = DateTimeOffset.UtcNow;
        OnDataUpdated?.Invoke();
    }
}
