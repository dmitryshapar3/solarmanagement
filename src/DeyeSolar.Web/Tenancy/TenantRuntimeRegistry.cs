using System.Collections.Concurrent;

namespace DeyeSolar.Web.Tenancy;

/// <summary>Only middleware/circuit gates with validated membership supply installation IDs here.</summary>
public sealed class TenantRuntimeRegistry : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<TenantRuntime>>> _runtimes = new(StringComparer.Ordinal);
    private readonly Func<string, CancellationToken, Task<TenantRuntime>> _create;
    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _enabledIds;
    private readonly CancellationTokenSource _stopping;
    private readonly object _sync = new();
    private Task? _disposeTask;
    public const int MaximumRuntimes = 256;

    public TenantRuntimeRegistry(TenantRuntimeFactory factory, IHostApplicationLifetime lifetime)
        : this(factory.CreateAsync, factory.EnabledInstallationIdsAsync, lifetime.ApplicationStopping) { }

    internal TenantRuntimeRegistry(Func<string, CancellationToken, Task<TenantRuntime>> create,
        Func<CancellationToken, Task<IReadOnlyList<string>>> enabledIds, CancellationToken stopping)
    {
        _create = create;
        _enabledIds = enabledIds;
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(stopping);
    }

    public async Task<TenantRuntime> GetAsync(string installationId, CancellationToken ct = default)
    {
        TenantDbContextFactory.ValidateId(installationId);
        Lazy<Task<TenantRuntime>> lazy;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (!_runtimes.TryGetValue(installationId, out lazy!))
            {
                if (_runtimes.Count >= MaximumRuntimes) throw new InvalidOperationException("Installation runtime capacity is currently unavailable.");
                lazy = new(() => _create(installationId, _stopping.Token), LazyThreadSafetyMode.ExecutionAndPublication);
                _runtimes[installationId] = lazy;
            }
        }
        try { return await lazy.Value.WaitAsync(ct).ConfigureAwait(false); }
        catch when (lazy.IsValueCreated && lazy.Value.IsFaulted)
        {
            _runtimes.TryRemove(new KeyValuePair<string, Lazy<Task<TenantRuntime>>>(installationId, lazy));
            throw;
        }
    }
    public TenantRuntime Get(string installationId) => GetAsync(installationId).GetAwaiter().GetResult();
    public T Resolve<T>(string installationId) where T : notnull => Get(installationId).Resolve<T>();
    public async Task RefreshSettingsAsync(string installationId, CancellationToken ct = default)
        => await (await GetAsync(installationId, ct).ConfigureAwait(false)).RefreshSettingsAsync(ct).ConfigureAwait(false);

    internal Task<IReadOnlyList<string>> EnabledInstallationIdsAsync(CancellationToken ct) => _enabledIds(ct);
    internal async Task RemoveDisabledAsync(IReadOnlySet<string> enabled)
    {
        foreach (var (id, lazy) in _runtimes)
            if (!enabled.Contains(id) && _runtimes.TryRemove(new KeyValuePair<string, Lazy<Task<TenantRuntime>>>(id, lazy)) && lazy.IsValueCreated)
                try { await (await lazy.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false); }
                catch { /* Failed initialization contains no usable runtime. */ }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync) return new(_disposeTask ??= StopAsync());
    }
    private async Task StopAsync()
    {
        _stopping.Cancel();
        foreach (var lazy in _runtimes.Values.Where(lazy => lazy.IsValueCreated))
            try { await (await lazy.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false); }
            catch { /* Continue disposing the other independent installations. */ }
        _runtimes.Clear();
        _stopping.Dispose();
    }
}
