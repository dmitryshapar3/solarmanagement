using DeyeSolar.Domain.Models;

namespace DeyeSolar.Domain.Services;

public class InverterDataSnapshot
{
    private InverterData? _current;

    public InverterData? Current => _current;

    public event Action? OnDataUpdated;

    public void Clear()
    {
        _current = null;
        OnDataUpdated?.Invoke();
    }

    public void Update(InverterData data)
    {
        _current = data;
        OnDataUpdated?.Invoke();
    }
}
