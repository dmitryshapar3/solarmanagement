namespace DeyeSolar.Web.Shared;

/// <summary>Owns the lifetime of read-only UI queries. Call from the component's renderer context.</summary>
public sealed class AsyncQueryScope : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private CancellationTokenSource? _current;
    private Task? _refresh;
    public bool IsDisposed { get; private set; }
    public bool IsLoading => !IsDisposed && _current is not null;
    public CancellationToken LifetimeToken => _lifetimeToken;
    public AsyncQueryScope() => _lifetimeToken = _lifetime.Token;

    public async Task RunAsync<T>(Func<CancellationToken, Task<T>> read, Action started, Action<T> received,
        Action<Exception> failed, Action finished)
    {
        if (IsDisposed) return;
        _current?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
        _current = request;
        bool IsCurrent() => !IsDisposed && ReferenceEquals(_current, request) && !request.IsCancellationRequested;
        try
        {
            started();
            var result = await read(request.Token);
            if (IsCurrent()) received(result);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception exception) { if (IsCurrent()) failed(exception); }
        finally
        {
            if (ReferenceEquals(_current, request))
            {
                _current = null;
                if (!IsDisposed && !request.IsCancellationRequested) finished();
            }
        }
    }

    public void StartRefresh(TimeProvider clock, TimeSpan interval, Func<Task> refresh)
    {
        if (IsDisposed || _refresh is not null) return;
        _refresh = RefreshAsync(clock, interval, refresh);
    }
    private async Task RefreshAsync(TimeProvider clock, TimeSpan interval, Func<Task> refresh)
    {
        using var timer = new PeriodicTimer(interval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetimeToken))
                await refresh().WaitAsync(_lifetimeToken);
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (IsDisposed) { }
        catch (InvalidOperationException) when (IsDisposed) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        _lifetime.Cancel();
        _current?.Cancel();
        if (_refresh is not null) await _refresh;
        _lifetime.Dispose();
    }
}
