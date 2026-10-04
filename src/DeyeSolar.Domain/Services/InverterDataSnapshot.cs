using DeyeSolar.Domain.Models;

namespace DeyeSolar.Domain.Services;

public class InverterDataSnapshot
{
    private InverterData? _current;
    private readonly object _sync = new();
    private long _epoch;

    public InverterData? Current { get { lock (_sync) return _current; } }
    public long Epoch { get { lock (_sync) return _epoch; } }

    public event Action? OnDataUpdated;

    public void Clear()
    {
        lock (_sync) { _epoch++; _current = null; }
        OnDataUpdated?.Invoke();
    }

    public void Update(InverterData data)
    {
        lock (_sync) _current = data;
        OnDataUpdated?.Invoke();
    }
    public bool TryUpdate(InverterData data, long expectedEpoch)
    {
        lock (_sync)
        {
            if (_epoch != expectedEpoch) return false;
            _current = data;
        }
        OnDataUpdated?.Invoke();
        return true;
    }
}
