using DeyeSolar.Domain.Models;

namespace DeyeSolar.Domain.Services;

public class DeviceStatusSnapshot
{
    private IReadOnlyList<DevicePowerInfo>? _current;
    private DateTimeOffset? _lastUpdated;
    private readonly object _sync = new();
    private long _epoch;

    public IReadOnlyList<DevicePowerInfo>? Current { get { lock (_sync) return _current; } }

    public DateTimeOffset? LastUpdated { get { lock (_sync) return _lastUpdated; } }
    public long Epoch { get { lock (_sync) return _epoch; } }

    public event Action? OnDataUpdated;

    public void Clear()
    {
        lock (_sync) { _epoch++; _current = null; _lastUpdated = null; }
        OnDataUpdated?.Invoke();
    }

    public void Update(IReadOnlyList<DevicePowerInfo> devices)
    {
        lock (_sync) { _current = devices; _lastUpdated = DateTimeOffset.UtcNow; }
        OnDataUpdated?.Invoke();
    }
    public bool TryUpdate(IReadOnlyList<DevicePowerInfo> devices, long expectedEpoch)
    {
        lock (_sync)
        {
            if (_epoch != expectedEpoch) return false;
            _current = devices;
            _lastUpdated = DateTimeOffset.UtcNow;
        }
        OnDataUpdated?.Invoke();
        return true;
    }

}
