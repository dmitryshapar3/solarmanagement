using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using DeyeSolar.Web.Workers;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Web.Services;

public interface IInverterRefreshService
{
    Task<InverterData> RefreshAsync(CancellationToken ct);
}

public sealed class InverterRefreshService : IInverterRefreshService, IDisposable, IAsyncDisposable
{
    private readonly IInverterDataSource _source;
    private readonly ExportReadingStore _readings;
    private readonly InverterDataSnapshot _snapshot;
    private readonly IOptionsMonitor<DeyeCloudOptions> _options;
    private readonly ILogger<InverterRefreshService> _logger;
    private readonly CancellationTokenSource _stop;
    private readonly IDisposable? _settingsSubscription;
    private readonly object _sync = new();
    private readonly HashSet<Operation> _operations = [];
    private Operation? _current;
    private Task? _disposeTask;

    public InverterRefreshService(IInverterDataSource source, ExportReadingStore readings,
        InverterDataSnapshot snapshot, IOptionsMonitor<DeyeCloudOptions> options,
        IHostApplicationLifetime lifetime, ILogger<InverterRefreshService> logger)
    {
        _source = source;
        _readings = readings;
        _snapshot = snapshot;
        _options = options;
        _logger = logger;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        _settingsSubscription = options.OnChange((_, _) => CancelChangedConfiguration());
    }

    public async Task<InverterData> RefreshAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var identity = DeyeRefreshIdentity.Capture(_options.CurrentValue);
        if (string.IsNullOrWhiteSpace(identity.DeviceSn))
            throw new InvalidOperationException("Select a Deye inverter in Settings.");
        Operation operation;
        Operation? replaced = null;
        var start = false;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            _stop.Token.ThrowIfCancellationRequested();
            if (_current is null || _current.Completion.Task.IsCompleted
                || _current.Cancellation.IsCancellationRequested || _current.Identity != identity)
            {
                replaced = _current;
                operation = new(identity, _stop.Token);
                _current = operation;
                _operations.Add(operation);
                operation.Work = RunAsync(operation);
                start = true;
            }
            else operation = _current;
            operation.Waiters++;
        }
        Cancel(replaced);
        if (start) operation.Start.TrySetResult(true);
        try
        {
            return await operation.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            var cancel = false;
            lock (_sync)
            {
                cancel = --operation.Waiters == 0 && !operation.Completion.Task.IsCompleted;
                // Detach before cancellation so a new caller cannot join abandoned work.
                if (cancel && ReferenceEquals(_current, operation)) _current = null;
            }
            if (cancel) Cancel(operation);
        }
    }

    private async Task RunAsync(Operation operation)
    {
        try
        {
            // Start is released outside the lock, including for synchronously completing sources.
            await operation.Start.Task.WaitAsync(operation.Cancellation.Token).ConfigureAwait(false);
            var data = await PollingRetryPolicy.ExecuteAsync(
                token => _source.ReadCurrentDataAsync(token), operation.Cancellation.Token,
                (exception, attempt, delay) => _logger.LogWarning(
                    "Deye refresh attempt {Attempt}/{MaxAttempts} failed ({ErrorType}); retrying in {RetryDelay}",
                    attempt, PollingRetryPolicy.DefaultMaxAttempts, exception.GetType().Name, delay)).ConfigureAwait(false);
            EnsureCurrent(operation);
            if (data.GridDeviceSn is { } gridDevice && gridDevice != operation.Identity.DeviceSn
                || data.SolarDeviceSn is { } solarDevice && solarDevice != operation.Identity.DeviceSn)
                throw new InvalidOperationException("Deye returned observations for a different inverter.");
            await _readings.SavePollingAsync(data, operation.Cancellation.Token).ConfigureAwait(false);
            EnsureCurrent(operation);
            try { _snapshot.Update(data); }
            catch (Exception ex)
            {
                // Persistence and the snapshot have already succeeded; notification failure is not a failed read.
                _logger.LogWarning("An inverter snapshot subscriber failed ({ErrorType})", ex.GetType().Name);
            }
            operation.Completion.TrySetResult(data);
        }
        catch (OperationCanceledException)
        {
            operation.Completion.TrySetCanceled();
        }
        catch (Exception ex)
        {
            operation.Completion.TrySetException(ex);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_current, operation)) _current = null;
                _operations.Remove(operation);
            }
            operation.Cancellation.Dispose();
        }
    }

    private void EnsureCurrent(Operation operation)
    {
        operation.Cancellation.Token.ThrowIfCancellationRequested();
        if (!operation.Identity.Matches(_options.CurrentValue))
            throw new InvalidOperationException("Deye settings changed during refresh. Try again.");
    }

    private void CancelChangedConfiguration()
    {
        Operation? changed;
        var identity = DeyeRefreshIdentity.Capture(_options.CurrentValue);
        lock (_sync) changed = _current is { } current && current.Identity != identity ? current : null;
        Cancel(changed);
    }

    private static void Cancel(Operation? operation)
    {
        try { operation?.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource<bool>? start = null;
        Task task;
        lock (_sync)
        {
            if (_disposeTask is null)
            {
                start = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeTask = StopAsync(_operations.Select(operation => operation.Work).ToArray(), start.Task);
            }
            task = _disposeTask;
        }
        start?.TrySetResult(true);
        return new(task);
    }

    private async Task StopAsync(Task[] operations, Task start)
    {
        // Cancellation callbacks may reenter callers; do not invoke them under the state lock.
        await start.ConfigureAwait(false);
        _settingsSubscription?.Dispose();
        _stop.Cancel();
        await Task.WhenAll(operations).ConfigureAwait(false);
        _stop.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private sealed class Operation(DeyeRefreshIdentity identity, CancellationToken stoppingToken)
    {
        public DeyeRefreshIdentity Identity { get; } = identity;
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        public TaskCompletionSource<bool> Start { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<InverterData> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Work { get; set; } = Task.CompletedTask;
        public int Waiters { get; set; }
    }
}

internal sealed record DeyeRefreshIdentity(string DeviceSn, string ConfigurationHash)
{
    public static DeyeRefreshIdentity Capture(DeyeCloudOptions options) => new(options.DeviceSn,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(options)))));
    public bool Matches(DeyeCloudOptions options) => this == Capture(options);
}
