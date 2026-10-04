using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using SolarManagement.Integrations.Contracts;

namespace SolarManagement.ProviderE2E;

public sealed class ProviderWorkerSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _responses;
    private readonly Task<string> _errors;
    private long _id;
    public JsonElement Handshake { get; private set; }

    private ProviderWorkerSession(Process process)
    {
        _process = process;
        _errors = process.StandardError.ReadToEndAsync();
        _responses = ReadResponsesAsync();
    }

    public static async Task<ProviderWorkerSession> StartAsync(string suffix, string providerId, MockVendorServer server,
        object values, IReadOnlyDictionary<string, string>? secrets = null, IReadOnlyList<string>? allowedOrigins = null)
    {
        var workerPath = Path.Combine(AppContext.BaseDirectory, "worker", "SolarManagement.ProviderE2E.Worker.dll");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        start.ArgumentList.Add(workerPath);
        start.ArgumentList.Add(suffix);
        start.ArgumentList.Add(server.BaseUri.AbsoluteUri);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start fixture worker.");
        var session = new ProviderWorkerSession(process);
        try
        {
            session.Handshake = await session.CallAsync("handshake", new { });
            if (session.Handshake.GetProperty("providerId").GetString() != providerId) throw new InvalidDataException("Wrong worker provider.");
            await session.CallAsync("initialize", new WorkerConfiguration(providerId, Guid.NewGuid(), 1, 1,
                new IntegrationDraftConfiguration(IntegrationJson.Element(values), secrets ?? new Dictionary<string, string>()),
                allowedOrigins ?? [], MaximumOperationTimeoutSeconds: 300));
            return session;
        }
        catch { await session.DisposeAsync(); throw; }
    }

    public async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken ct = default)
    {
        var response = await CallResponseAsync(method, parameters, ct);
        if (response.TryGetProperty("error", out _)) throw new InvalidOperationException("Worker rejected operation: " + method);
        return response.GetProperty("result").Clone();
    }

    public async Task<JsonElement> CallResponseAsync(string method, object parameters, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _id);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion)) throw new InvalidOperationException("Duplicate fixture request ID.");
        try
        {
            await SendAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, ct);
            try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await SendAsync(new { jsonrpc = "2.0", method = "$/cancelRequest", @params = new { id } }, CancellationToken.None);
                throw;
            }
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task SendAsync(object message, CancellationToken ct)
    {
        await _writes.WaitAsync(ct);
        try { await LengthFramedJson.WriteAsync(_process.StandardInput.BaseStream, message, 1024 * 1024, ct); }
        finally { _writes.Release(); }
    }

    private async Task ReadResponsesAsync()
    {
        try
        {
            while (await LengthFramedJson.ReadAsync(_process.StandardOutput.BaseStream, 1024 * 1024, _shutdown.Token) is { } frame)
                if (frame.TryGetProperty("id", out var id) && _pending.TryGetValue(id.GetInt64(), out var response)) response.TrySetResult(frame);
            if (!_shutdown.IsCancellationRequested)
            {
                await _process.WaitForExitAsync();
                var error = new InvalidOperationException("Fixture worker exited: " + await _errors);
                foreach (var pending in _pending.Values) pending.TrySetException(error);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error) { foreach (var pending in _pending.Values) pending.TrySetException(error); }
    }

    public async ValueTask DisposeAsync()
    {
        _process.StandardInput.Close();
        try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { _process.Kill(entireProcessTree: true); await _process.WaitForExitAsync(); }
        _shutdown.Cancel();
        await _responses;
        _shutdown.Dispose();
        _writes.Dispose();
        _process.Dispose();
    }
}
