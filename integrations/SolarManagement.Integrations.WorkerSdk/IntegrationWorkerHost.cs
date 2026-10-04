using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SolarManagement.Integrations.Contracts;

namespace SolarManagement.Integrations.WorkerSdk;

public interface IIntegrationWorkerProvider : IAsyncDisposable
{
    int MinimumOperationTimeoutSeconds => 0;
    Task<JsonElement> InvokeAsync(string method, JsonElement parameters, CancellationToken ct);
}
public static class IntegrationWorkerHost
{
    public static async Task RunAsync(string providerId, IReadOnlyList<string> operations,
        Func<WorkerConfiguration, IIntegrationWorkerProvider> factory, CancellationToken ct = default)
    {
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        using var writes = new SemaphoreSlim(1, 1);
        using var calls = new SemaphoreSlim(1, 1);
        var pending = new ConcurrentDictionary<long, (CancellationTokenSource Cancellation, Task Work)>();
        IIntegrationWorkerProvider? provider = null;
        async Task RespondAsync(object response)
        {
            await writes.WaitAsync(shutdown.Token);
            try { await LengthFramedJson.WriteAsync(output, response, 1024 * 1024, shutdown.Token); }
            finally { writes.Release(); }
        }
        async Task ExecuteAsync(long id, string method, JsonElement parameters, CancellationToken token)
        {
            try
            {
                await calls.WaitAsync(token);
                try
                {
                    JsonElement result;
                    if (method == "handshake") result = IntegrationJson.Element(new WorkerHandshake(1, providerId, operations));
                    else if (method == "initialize")
                    {
                        if (provider is not null) throw new InvalidOperationException("A worker session cannot be reinitialized.");
                        var configuration = parameters.Deserialize<WorkerConfiguration>(IntegrationJson.Options)
                            ?? throw new InvalidDataException("Configuration is missing.");
                        if (configuration.ProviderId != providerId || configuration.InstanceId == Guid.Empty
                            || configuration.ConfigurationRevision < 1 || configuration.Generation < 1
                            || configuration.MaximumOperationTimeoutSeconds is < 1 or > 300
                            || configuration.Configuration.Secrets.Count > 32
                            || configuration.Configuration.Secrets.Any(item => item.Key.Length > 128 || item.Value.Length > 8192))
                            throw new InvalidDataException("Configuration session is invalid.");
                        provider = factory(configuration);
                        if (provider.MinimumOperationTimeoutSeconds < 0
                            || provider.MinimumOperationTimeoutSeconds > configuration.MaximumOperationTimeoutSeconds)
                            throw new InvalidDataException("Provider operation deadline exceeds the host bound.");
                        result = IntegrationJson.Element(new { initialized = true, minimumOperationTimeoutSeconds = provider.MinimumOperationTimeoutSeconds });
                    }
                    else
                    {
                        if (provider is null || !operations.Contains(method, StringComparer.Ordinal))
                            throw new InvalidOperationException("The worker operation is unavailable.");
                        if (method == "oauth.begin") IntegrationOAuthProtocol.ValidateBegin(parameters.Deserialize<IntegrationOAuthBeginRequest>(IntegrationJson.Options)
                            ?? throw new InvalidDataException("OAuth request is missing."));
                        if (method == "oauth.complete") IntegrationOAuthProtocol.ValidateComplete(parameters.Deserialize<IntegrationOAuthCompleteRequest>(IntegrationJson.Options)
                            ?? throw new InvalidDataException("OAuth request is missing."));
                        result = await provider.InvokeAsync(method, parameters, token);
                    }
                    await RespondAsync(new { jsonrpc = "2.0", id, result });
                }
                finally { calls.Release(); }
            }
            catch (Exception)
            {
                // Vendor responses and exception messages can contain credentials.
                if (!shutdown.IsCancellationRequested)
                    await RespondAsync(new { jsonrpc = "2.0", id, error = new { code = -32000, message = "Integration operation failed." } });
            }
        }
        try
        {
            while (await LengthFramedJson.ReadAsync(input, 1024 * 1024, shutdown.Token) is { } request)
            {
                if (!request.TryGetProperty("jsonrpc", out var version) || version.GetString() != "2.0"
                    || !request.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Invalid worker request.");
                var method = methodElement.GetString()!;
                var parameters = request.TryGetProperty("params", out var args) ? args.Clone() : IntegrationJson.Element(new { });
                if (method == "$/cancelRequest")
                {
                    if (parameters.TryGetProperty("id", out var canceled) && canceled.TryGetInt64(out var canceledId)
                        && pending.TryGetValue(canceledId, out var operation)) operation.Cancellation.Cancel();
                    continue;
                }
                if (!request.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id)
                    || pending.ContainsKey(id) || pending.Count >= 64)
                    throw new InvalidDataException("Invalid or excessive worker request IDs.");
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var work = Task.Run(async () =>
                {
                    await ready.Task;
                    try { await ExecuteAsync(id, method, parameters, cancellation.Token); }
                    finally { pending.TryRemove(id, out _); cancellation.Dispose(); }
                }, CancellationToken.None);
                pending[id] = (cancellation, work);
                ready.SetResult();
            }
        }
        finally
        {
            shutdown.Cancel();
            await Task.WhenAll(pending.Values.Select(item => item.Work));
            if (provider is not null) await provider.DisposeAsync();
        }
    }
}
public sealed class FixedOptionsMonitor<T>(T value) : IOptionsMonitor<T> where T : class
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
