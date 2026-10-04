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
    private readonly Dictionary<string, Task> _retired = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _paused = new(StringComparer.Ordinal);
    private readonly HashSet<string> _disabled = new(StringComparer.Ordinal);
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
        Task<TenantRuntime> initialization;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null || _stopping.IsCancellationRequested, this);
            if (_retired.ContainsKey(installationId) || _paused.ContainsKey(installationId) || _disabled.Contains(installationId))
                throw new InvalidOperationException("The installation runtime is unavailable.");
            if (!_runtimes.TryGetValue(installationId, out lazy!))
            {
                if (_runtimes.Count >= MaximumRuntimes) throw new InvalidOperationException("Installation runtime capacity is currently unavailable.");
                lazy = new(() => _create(installationId, _stopping.Token), LazyThreadSafetyMode.ExecutionAndPublication);
                _runtimes[installationId] = lazy;
            }
            // Initialization starts while admission is held; a concurrent pause must be able to drain it.
            initialization = lazy.Value;
        }
        try
        {
            var runtime = await initialization.WaitAsync(ct).ConfigureAwait(false);
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
                if (!_runtimes.TryGetValue(installationId, out var admitted) || !ReferenceEquals(admitted, lazy))
                    throw new InvalidOperationException("The installation runtime is unavailable.");
            }
            return runtime;
        }
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
        List<Lazy<Task<TenantRuntime>>> removed = [];
        lock (_sync)
        {
            _disabled.RemoveWhere(enabled.Contains);
            foreach (var (id, lazy) in _runtimes)
                if (!enabled.Contains(id) && _runtimes.TryRemove(new KeyValuePair<string, Lazy<Task<TenantRuntime>>>(id, lazy)))
                { _disabled.Add(id); removed.Add(lazy); }
        }
        foreach (var lazy in removed) await DisposeRuntimeAsync(lazy).ConfigureAwait(false);
    }

    /// <summary>Closes admission immediately and drains existing work. Cancellation only cancels the caller's wait.</summary>
    public Task StopInstallationAsync(string installationId, CancellationToken ct = default)
    {
        TenantDbContextFactory.ValidateId(installationId);
        Task stopped;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (!_retired.TryGetValue(installationId, out stopped!))
            {
                if (!_paused.Remove(installationId, out stopped!))
                {
                    _runtimes.TryRemove(installationId, out var lazy);
                    stopped = lazy is null ? Task.CompletedTask : DisposeRuntimeAsync(lazy);
                }
                _retired.Add(installationId, stopped);
            }
        }
        return stopped.WaitAsync(ct);
    }

    public Task PauseInstallationAsync(string installationId, CancellationToken ct = default)
    {
        TenantDbContextFactory.ValidateId(installationId);
        Task paused;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_retired.TryGetValue(installationId, out var retired)) return retired.WaitAsync(ct);
            if (!_paused.TryGetValue(installationId, out paused!))
            {
                _runtimes.TryRemove(installationId, out var lazy);
                paused = lazy is null ? Task.CompletedTask : DisposeRuntimeAsync(lazy);
                _paused.Add(installationId, paused);
            }
        }
        return paused.WaitAsync(ct);
    }

    public void ResumeInstallation(string installationId)
    {
        TenantDbContextFactory.ValidateId(installationId);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_retired.ContainsKey(installationId)) throw new InvalidOperationException("The installation runtime is retired.");
            if (_paused.TryGetValue(installationId, out var paused) && !paused.IsCompletedSuccessfully)
                throw new InvalidOperationException("The installation runtime is still stopping.");
            _paused.Remove(installationId);
            _disabled.Remove(installationId);
        }
    }

    private static async Task DisposeRuntimeAsync(Lazy<Task<TenantRuntime>> lazy)
    {
        if (!lazy.IsValueCreated) return;
        TenantRuntime runtime;
        try { runtime = await lazy.Value.ConfigureAwait(false); }
        catch { return; /* Initialization did not produce a usable runtime. */ }
        await runtime.DisposeAsync().ConfigureAwait(false);
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
        foreach (var stop in _retired.Values) await stop.ConfigureAwait(false);
        foreach (var stop in _paused.Values) await stop.ConfigureAwait(false);
        _stopping.Dispose();
    }
}
