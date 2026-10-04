using Microsoft.Extensions.Options;
using SolarManagement.Inverters.Contracts;

namespace DeyeSolar.Web.Integrations;

public sealed class InverterSelectionMonitor(IIntegrationRegistry registry) : IOptionsMonitor<InverterConnectionOptions>
{
    private readonly object _sync = new();
    private InverterConnectionOptions _current = new();
    private long _epoch;
    private long _refresh;
    private event Action<InverterConnectionOptions, string?>? Changed;
    public InverterConnectionOptions CurrentValue { get { lock (_sync) return _current; } }
    public InverterConnectionOptions Get(string? name) => CurrentValue;
    public IDisposable OnChange(Action<InverterConnectionOptions, string?> listener)
    {
        lock (_sync) Changed += listener;
        return new Subscription(() => { lock (_sync) Changed -= listener; });
    }
    public void Invalidate()
    {
        Action<InverterConnectionOptions, string?>? notify;
        InverterConnectionOptions value = new();
        lock (_sync) { _epoch++; _current = value; notify = Changed; }
        foreach (var listener in notify?.GetInvocationList() ?? [])
        {
            try { ((Action<InverterConnectionOptions, string?>)listener)(value, null); }
            catch { /* Notify remaining consumers after configuration invalidation. */ }
        }
    }
    public async Task RefreshAsync(CancellationToken ct)
    {
        long epoch;
        long refresh;
        lock (_sync) { epoch = _epoch; refresh = ++_refresh; }
        var binding = await registry.GetPrimaryInverterAsync(ct);
        var snapshot = binding is null ? null : await registry.GetSnapshotAsync(binding.InstanceId, ct);
        var value = new InverterConnectionOptions
        {
            DeviceKey = binding?.Id.ToString("D") ?? "",
            ConnectionIdentity = snapshot is null ? "" : $"{snapshot.Instance.Id:D}/{snapshot.Instance.PackageDigest}",
            Revision = snapshot?.Instance.Revision ?? 0,
            Generation = snapshot?.Instance.Generation ?? 0
        };
        Action<InverterConnectionOptions, string?>? notify;
        lock (_sync)
        {
            if (epoch != _epoch || refresh != _refresh) return;
            if (_current.DeviceKey == value.DeviceKey && _current.ConnectionIdentity == value.ConnectionIdentity
                && _current.Revision == value.Revision && _current.Generation == value.Generation) return;
            _current = value;
            notify = Changed;
        }
        foreach (var listener in notify?.GetInvocationList() ?? [])
        {
            try { ((Action<InverterConnectionOptions, string?>)listener)(value, null); }
            catch { /* Persistence does not depend on a notification consumer. */ }
        }
    }
    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;
        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
