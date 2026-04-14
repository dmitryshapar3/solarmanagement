using Microsoft.Extensions.Options;

namespace DeyeSolar.Web.Tests;

internal sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
{
    private T _currentValue;

    public TestOptionsMonitor(T currentValue)
    {
        _currentValue = currentValue;
    }

    public T CurrentValue => _currentValue;

    public T Get(string? name) => _currentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;

    public void Set(T value) => _currentValue = value;
}
