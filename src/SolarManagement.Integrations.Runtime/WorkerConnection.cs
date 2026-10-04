using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using SolarManagement.Integrations.Contracts;

namespace SolarManagement.Integrations.Runtime;

internal sealed class WorkerConnection : IAsyncDisposable
{
    private readonly Process _process;
    private readonly IntegrationRuntimeOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Task _reader;
    private readonly Task _stderr;
    private long _nextId;
    private int _disposed;
    private int _requestTimeoutSeconds;
    private readonly TaskCompletionSource _disposeCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public WorkerConnection(IntegrationInstalledPackage package, IntegrationRuntimeOptions options)
    {
        _options = options;
        _requestTimeoutSeconds = options.RequestTimeoutSeconds;
        var start = new ProcessStartInfo(options.DotnetExecutable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = package.ArtifactPath
        };
        start.ArgumentList.Add(IntegrationPackageStore.Within(package.ArtifactPath, package.Manifest.EntryPoint));
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "SystemRoot", "WINDIR", "DOTNET_ROOT", "DOTNET_ROOT_X64" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["DOTNET_NOLOGO"] = "true";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "true";
        _process = Process.Start(start) ?? throw new InvalidOperationException("Worker could not be started.");
        _reader = ReadResponsesAsync();
        _stderr = DrainErrorsAsync();
    }
    public bool IsAlive => Volatile.Read(ref _disposed) == 0 && !_process.HasExited && !_reader.IsCompleted;
    public void NegotiateRequestTimeout(int minimumSeconds)
    {
        if (minimumSeconds < 0 || minimumSeconds > _options.MaximumNegotiatedRequestTimeoutSeconds)
            throw new InvalidDataException("Worker operation deadline exceeds the configured bound.");
        _requestTimeoutSeconds = Math.Max(_options.RequestTimeoutSeconds, minimumSeconds);
    }
    public async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(_requestTimeoutSeconds));
        try
        {
            await SendAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, deadline.Token);
            return await completion.Task.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            using var cancellationDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await SendAsync(new { jsonrpc = "2.0", method = "$/cancelRequest", @params = new { id } }, cancellationDeadline.Token); }
            catch (Exception) { }
            // An abandoned write must not keep the process or its session alive indefinitely.
            await DisposeAsync();
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException("The integration worker did not complete within its deadline.");
        }
        finally { _pending.TryRemove(id, out _); }
    }
    private async Task SendAsync<T>(T request, CancellationToken ct)
    {
        await _writes.WaitAsync(ct);
        try { await LengthFramedJson.WriteAsync(_process.StandardInput.BaseStream, request, _options.MaximumFrameBytes, ct); }
        finally { _writes.Release(); }
    }
    private async Task ReadResponsesAsync()
    {
        try
        {
            while (await LengthFramedJson.ReadAsync(_process.StandardOutput.BaseStream, _options.MaximumFrameBytes, _stop.Token) is { } response)
            {
                if (!response.TryGetProperty("jsonrpc", out var version) || version.GetString() != "2.0"
                    || !response.TryGetProperty("id", out var id) || !id.TryGetInt64(out var requestId)
                    || !_pending.TryGetValue(requestId, out var completion))
                    throw new InvalidDataException("Worker returned an invalid or unsolicited response.");
                if (response.TryGetProperty("error", out _)) completion.TrySetException(new InvalidOperationException("The integration rejected the operation."));
                else if (response.TryGetProperty("result", out var result)) completion.TrySetResult(result.Clone());
                else throw new InvalidDataException("Worker response has no result.");
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        catch (Exception) { }
        finally
        {
            foreach (var completion in _pending.Values)
                completion.TrySetException(new IOException("The integration worker session ended."));
        }
    }
    private async Task DrainErrorsAsync()
    {
        var buffer = new char[1024];
        try { while (await _process.StandardError.ReadAsync(buffer.AsMemory(), _stop.Token) > 0) { } }
        catch (Exception) when (_stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { await _disposeCompleted.Task; return; }
        try
        {
            _stop.Cancel();
            try { _process.StandardInput.Close(); } catch (Exception) { }
            try
            {
                using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _process.WaitForExitAsync(drain.Token);
            }
            catch (OperationCanceledException) { try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
            catch (InvalidOperationException) { }
            await Task.WhenAll(_reader, _stderr);
            _process.Dispose();
            _stop.Dispose();
            _disposeCompleted.TrySetResult();
        }
        catch (Exception ex) { _disposeCompleted.TrySetException(ex); throw; }
    }
}
