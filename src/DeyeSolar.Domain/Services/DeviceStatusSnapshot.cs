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
}
