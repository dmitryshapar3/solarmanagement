using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SolarManagement.Integrations.Contracts;

namespace SolarManagement.Integrations.Runtime;

public sealed class IntegrationWorkerRuntime : IIntegrationRuntimeExecutor, IIntegrationSetupExecutor, IAsyncDisposable
{
    private readonly IIntegrationPackageManager _packages;
    private readonly IntegrationRuntimeOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, SessionWorker> _workers = [];
    private readonly Dictionary<Guid, KnownSession> _known = [];
    private readonly Dictionary<Guid, (int Failures, DateTimeOffset RetryAt)> _failures = [];
    private readonly CancellationTokenSource _stop = new();
    private int _queued;
    private int _setupWorkers;
    private bool _disposed;
    private int _disposing;
    private readonly TaskCompletionSource _disposeCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IntegrationWorkerRuntime(IIntegrationPackageManager packages, IOptions<IntegrationRuntimeOptions> options)
    { _packages = packages; _options = options.Value; _options.Validate(); }
    public async Task<JsonElement> InvokeAsync(IntegrationSession session, string method, JsonElement parameters, CancellationToken ct)
    {
        if (method is not ("inverter.read" or "inverter.history" or "socket.read" or "socket.set" or "socket.inventory" or "socket.result"))
            throw new ArgumentException("Unsupported device operation.", nameof(method));
        EnterQueue();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        SessionWorker? worker = null;
        try
        {
            await _gate.WaitAsync(operation.Token);
            try { ObjectDisposedException.ThrowIf(_disposed, this); worker = await GetWorkerAsync(session, operation.Token); worker.ActiveCalls++; }
            finally { _gate.Release(); }
            await worker.Calls.WaitAsync(operation.Token);
            try
            {
                var result = await worker.Connection.CallAsync(method, parameters, operation.Token);
                await _gate.WaitAsync(CancellationToken.None);
                try
                {
                    worker.LastUsed = DateTimeOffset.UtcNow;
                    _failures.Remove(session.InstanceId);
                    if (method is "socket.set" or "socket.result")
                    {
                        if (!parameters.TryGetProperty("commandId", out var requestedCommand)
                            || requestedCommand.ValueKind != JsonValueKind.String
                            || !Guid.TryParse(requestedCommand.GetString(), out var requestedId)
                            || !result.TryGetProperty("commandId", out var command) || command.ValueKind != JsonValueKind.String
                            || !Guid.TryParse(command.GetString(), out var returnedId) || returnedId != requestedId
                            || !result.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String)
                            throw new InvalidDataException("Worker command receipt does not match the requested identity.");
                        var key = requestedId.ToString("D");
                        if (string.Equals(status.GetString(), "Pending", StringComparison.OrdinalIgnoreCase)) worker.PendingCommands.Add(key);
                        else worker.PendingCommands.Remove(key);
                    }
                }
                finally { _gate.Release(); }
                return result;
            }
            catch
            {
                await _gate.WaitAsync(CancellationToken.None);
                try
                {
                    if (_workers.TryGetValue(session.InstanceId, out var active) && ReferenceEquals(active, worker)) _workers.Remove(session.InstanceId);
                    if (!ct.IsCancellationRequested && !_stop.IsCancellationRequested) RecordFailure(session.InstanceId);
                }
                finally { _gate.Release(); }
                await worker.Connection.DisposeAsync();
                throw;
            }
            finally { worker.Calls.Release(); }
        }
        finally
        {
            if (worker is not null)
            {
                await _gate.WaitAsync(CancellationToken.None);
                try { worker.ActiveCalls--; }
                finally { _gate.Release(); }
            }
            Interlocked.Decrement(ref _queued);
        }
    }
    private void EnterQueue()
    {
        if (Interlocked.Increment(ref _queued) <= _options.MaximumQueuedCalls) return;
        Interlocked.Decrement(ref _queued);
        throw new InvalidOperationException("Integration queue is full.");
    }
    private async Task<SessionWorker> GetWorkerAsync(IntegrationSession session, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(session.InstallationId) || session.InstanceId == Guid.Empty || session.ConfigurationRevision < 1 || session.Generation < 1)
            throw new ArgumentException("An authoritative integration session is required.");
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(session.Configuration, IntegrationJson.Options)));
        if (_known.TryGetValue(session.InstanceId, out var known))
        {
            if (known.InstallationId != session.InstallationId) throw new InvalidOperationException("Integration instance belongs to another installation.");
            if (known.ConfigurationRevision > session.ConfigurationRevision || known.Generation > session.Generation
                || known.Disabled && known.Generation >= session.Generation)
                throw new InvalidOperationException("The integration session was replaced or disabled.");
            if (known.Generation == session.Generation && (known.Package != session.Package || known.ConfigurationRevision != session.ConfigurationRevision))
                throw new InvalidOperationException("A changed runtime configuration requires a new generation.");
            if (known.ConfigurationRevision == session.ConfigurationRevision && (known.ConfigurationHash != hash || known.Package != session.Package))
                throw new InvalidOperationException("Configuration revision is immutable.");
        }
        else if (_known.Count >= _options.MaximumRememberedInstances)
            throw new InvalidOperationException("Integration instance admission is full.");
        if (_workers.TryGetValue(session.InstanceId, out var current))
        {
            if (SameIdentity(current.Identity, session) && current.Connection.IsAlive) return current;
            if (current.ActiveCalls > 0) throw new InvalidOperationException("The previous generation is still draining.");
            _workers.Remove(session.InstanceId);
            await current.Connection.DisposeAsync();
        }
        if (_failures.TryGetValue(session.InstanceId, out var failure) && failure.RetryAt > DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Integration worker is backing off after a failure.");
        await EnsureCapacityAsync();
        // Remember only fencing metadata and a fingerprint; evicted sessions must not retain their credentials.
        _known[session.InstanceId] = new(session.InstallationId, session.Package, session.ConfigurationRevision, session.Generation, hash, false);
        try
        {
            var connection = await StartAsync(session.Package, session.InstanceId, session.ConfigurationRevision,
                session.Generation, session.Configuration, ct);
            var worker = new SessionWorker(session, connection);
            _workers[session.InstanceId] = worker;
            return worker;
        }
        catch { if (!ct.IsCancellationRequested) RecordFailure(session.InstanceId); throw; }
    }
    private async Task EnsureCapacityAsync()
    {
        foreach (var idle in _workers.Where(item => item.Value.ActiveCalls == 0 && item.Value.PendingCommands.Count == 0
            && DateTimeOffset.UtcNow - item.Value.LastUsed > TimeSpan.FromSeconds(_options.IdleTimeoutSeconds)).ToArray())
        { _workers.Remove(idle.Key); await idle.Value.Connection.DisposeAsync(); }
        if (_workers.Count + _setupWorkers < _options.MaximumWorkers) return;
        var inactive = _workers.Where(item => item.Value.ActiveCalls == 0 && item.Value.PendingCommands.Count == 0).OrderBy(item => item.Value.LastUsed).FirstOrDefault();
        if (inactive.Value is null) throw new InvalidOperationException("All integration workers are busy.");
        _workers.Remove(inactive.Key);
        await inactive.Value.Connection.DisposeAsync();
    }
    private void RecordFailure(Guid instanceId)
    {
        var count = Math.Min(6, _failures.GetValueOrDefault(instanceId).Failures + 1);
        _failures[instanceId] = (count, DateTimeOffset.UtcNow.AddSeconds(Math.Pow(2, count)));
    }
    private static bool SameIdentity(IntegrationSession first, IntegrationSession second) => first.InstanceId == second.InstanceId
        && first.InstallationId == second.InstallationId && first.Package == second.Package
        && first.ConfigurationRevision == second.ConfigurationRevision && first.Generation == second.Generation;
    private async Task<WorkerConnection> StartAsync(ProviderPackageIdentity identity, Guid instance, long revision,
        long generation, IntegrationDraftConfiguration configuration, CancellationToken ct)
    {
        var package = await _packages.ResolveAsync(identity, ct);
        var worker = new WorkerConnection(package, _options);
        try
        {
            var handshake = (await worker.CallAsync("handshake", new { }, ct)).Deserialize<WorkerHandshake>(IntegrationJson.Options);
            if (handshake?.WireVersion != 1 || handshake.ProviderId != identity.ProviderId)
                throw new InvalidDataException("Integration worker handshake does not match its signed package.");
            var initialized = await worker.CallAsync("initialize", new WorkerConfiguration(identity.ProviderId, instance, revision,
                generation, configuration, package.Manifest.AllowedOrigins, _options.MaximumNegotiatedRequestTimeoutSeconds), ct);
            if (initialized.TryGetProperty("minimumOperationTimeoutSeconds", out var timeout))
            {
                if (!timeout.TryGetInt32(out var minimum)) throw new InvalidDataException("Worker deadline is invalid.");
                worker.NegotiateRequestTimeout(minimum);
            }
            return worker;
        }
        catch { await worker.DisposeAsync(); throw; }
    }
    private async Task<T> SetupAsync<T>(ProviderPackageIdentity package, IntegrationDraftConfiguration draft,
        string method, object parameters, CancellationToken ct)
    {
        EnterQueue();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        var reserved = false;
        try
        {
            await _gate.WaitAsync(operation.Token);
            try { ObjectDisposedException.ThrowIf(_disposed, this); await EnsureCapacityAsync(); _setupWorkers++; reserved = true; }
            finally { _gate.Release(); }
            await using var worker = await StartAsync(package, Guid.NewGuid(), 1, 1, draft, operation.Token);
            return (await worker.CallAsync(method, parameters, operation.Token)).Deserialize<T>(IntegrationJson.Options)
                ?? throw new InvalidDataException("Integration setup returned an invalid result.");
        }
        finally
        {
            if (reserved)
            {
                await _gate.WaitAsync(CancellationToken.None);
                try { _setupWorkers--; }
                finally { _gate.Release(); }
            }
            Interlocked.Decrement(ref _queued);
        }
    }
    public Task<IntegrationTestResult> TestAsync(ProviderPackageIdentity package, IntegrationDraftConfiguration draft, CancellationToken ct)
        => SetupAsync<IntegrationTestResult>(package, draft, "test", new { }, ct);
    public async Task<IntegrationOAuthBeginResult> BeginAuthorizationAsync(ProviderPackageIdentity package,
        IntegrationDraftConfiguration draft, IntegrationOAuthBeginRequest request, CancellationToken ct)
    {
        IntegrationOAuthProtocol.ValidateBegin(request);
        var installed = await OAuthPackageAsync(package, ct);
        var result = await SetupAsync<IntegrationOAuthBeginResult>(package, draft, "oauth.begin", request, ct);
        IntegrationOAuthProtocol.ValidateAuthorizationUrl(result.AuthorizationUrl, request, installed.Manifest.AllowedOrigins);
        return result;
    }
    public async Task<IntegrationOAuthCompleteResult> CompleteAuthorizationAsync(ProviderPackageIdentity package,
        IntegrationDraftConfiguration draft, IntegrationOAuthCompleteRequest request, CancellationToken ct)
    {
        IntegrationOAuthProtocol.ValidateComplete(request);
        var installed = await OAuthPackageAsync(package, ct);
        var result = await SetupAsync<IntegrationOAuthCompleteResult>(package, draft, "oauth.complete", request, ct);
        var descriptor = installed.Manifest.Descriptor;
        if (result.PublicValues.ValueKind != JsonValueKind.Object || result.SecretValues is null || result.SecretValues.Count > 32
            || result.AccountIdentity?.Length > 256 || result.AccountIdentity is not null && string.IsNullOrWhiteSpace(result.AccountIdentity))
            throw new InvalidDataException("OAuth returned invalid configuration or identity.");
        var publicKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in result.PublicValues.EnumerateObject())
        {
            var field = descriptor.Fields.SingleOrDefault(field => field.Key == property.Name);
            if (!publicKeys.Add(property.Name) || field is null || IntegrationUiConditions.IsSecret(field))
                throw new InvalidDataException("OAuth public output contains an undeclared or secret field.");
            IntegrationDescriptorValidator.ValidateValue(field, property.Value);
        }
        foreach (var (key, value) in result.SecretValues)
            if (!descriptor.OAuthDefinition!.SecretFieldKeys.Contains(key, StringComparer.Ordinal)
                || string.IsNullOrWhiteSpace(value) || value.Length > 8192)
                throw new InvalidDataException("OAuth secret output is outside the declared whitelist.");
        if (!result.Success && (publicKeys.Count != 0 || result.SecretValues.Count != 0 || result.AccountIdentity is not null))
            throw new InvalidDataException("An unsuccessful OAuth result must not return credentials or configuration.");
        return result;
    }
    private async Task<IntegrationInstalledPackage> OAuthPackageAsync(ProviderPackageIdentity package, CancellationToken ct)
    {
        var installed = await _packages.ResolveAsync(package, ct);
        if (installed.Manifest.Descriptor.OAuthDefinition is null) throw new NotSupportedException("This provider does not support OAuth.");
        return installed;
    }
    public async Task<IReadOnlyList<IntegrationDiscoveredDevice>> DiscoverAsync(ProviderPackageIdentity package,
        IntegrationDraftConfiguration draft, IntegrationDiscoveryQuery query, CancellationToken ct)
    {
        var devices = await SetupAsync<IntegrationDiscoveredDevice[]>(package, draft, "discover", query, ct);
        if (devices.Length > 200 || devices.Any(device => string.IsNullOrWhiteSpace(device.RemoteId) || device.RemoteId.Length > 128
            || device.Channel?.Length > 32 || device.Name.Length > 256 || device.Kind is not ("inverter" or "socket" or "group")))
            throw new InvalidDataException("Integration discovery exceeded its resource limits.");
        return devices;
    }
    public async Task StopAsync(Guid instanceId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        SessionWorker? worker;
        try
        {
            _workers.Remove(instanceId, out worker);
            if (_known.TryGetValue(instanceId, out var known)) _known[instanceId] = known with { Disabled = true };
            _failures.Remove(instanceId);
        }
        finally { _gate.Release(); }
        if (worker is not null) await worker.Connection.DisposeAsync();
    }
    public async Task ReleaseCommandTrackingAsync(Guid instanceId, Guid commandId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_workers.TryGetValue(instanceId, out var worker)) worker.PendingCommands.Remove(commandId.ToString("D"));
        }
        finally { _gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposing, 1) != 0) { await _disposeCompleted.Task; return; }
        try
        {
            _stop.Cancel();
            await _gate.WaitAsync();
            SessionWorker[] workers;
            try { _disposed = true; workers = _workers.Values.ToArray(); _workers.Clear(); }
            finally { _gate.Release(); }
            await Task.WhenAll(workers.Select(worker => worker.Connection.DisposeAsync().AsTask()));
            while (Volatile.Read(ref _queued) > 0) await Task.Delay(10);
            _disposeCompleted.TrySetResult();
        }
        catch (Exception ex) { _disposeCompleted.TrySetException(ex); throw; }
    }
    private sealed record KnownSession(string InstallationId, ProviderPackageIdentity Package, long ConfigurationRevision,
        long Generation, string ConfigurationHash, bool Disabled);
    private sealed class SessionWorker(IntegrationSession identity, WorkerConnection connection)
    {
        public IntegrationSession Identity { get; } = identity;
        public WorkerConnection Connection { get; } = connection;
        public SemaphoreSlim Calls { get; } = new(1, 1);
        public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;
        public int ActiveCalls { get; set; }
        public HashSet<string> PendingCommands { get; } = new(StringComparer.Ordinal);
    }
}

public static class IntegrationRuntimeServiceCollectionExtensions
{
    public static IServiceCollection AddIntegrationRuntime(this IServiceCollection services)
    {
        services.AddSingleton<IntegrationPackageStore>();
        services.AddSingleton<IntegrationPackageBootstrap>();
        services.AddSingleton<IIntegrationPackageManager>(sp => sp.GetRequiredService<IntegrationPackageStore>());
        services.AddSingleton<IIntegrationProviderCatalog>(sp => sp.GetRequiredService<IntegrationPackageStore>());
        services.AddSingleton<IntegrationWorkerRuntime>();
        services.AddSingleton<IIntegrationRuntimeExecutor>(sp => sp.GetRequiredService<IntegrationWorkerRuntime>());
        services.AddSingleton<IIntegrationSetupExecutor>(sp => sp.GetRequiredService<IntegrationWorkerRuntime>());
        return services;
    }
}
