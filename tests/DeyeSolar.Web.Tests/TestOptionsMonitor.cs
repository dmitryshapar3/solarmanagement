using Microsoft.Extensions.Options;

namespace DeyeSolar.Web.Tests;

internal sealed class FixedOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

internal sealed class MutableOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    private event Action<T, string?>? Changed;
    public T CurrentValue { get; private set; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable OnChange(Action<T, string?> listener)
    {
        Changed += listener;
        return new Subscription(() => Changed -= listener);
    }
    public void Change(T value) { CurrentValue = value; Changed?.Invoke(value, null); }
    private sealed class Subscription(Action remove) : IDisposable
    {
        public void Dispose() => remove();
    }
}
