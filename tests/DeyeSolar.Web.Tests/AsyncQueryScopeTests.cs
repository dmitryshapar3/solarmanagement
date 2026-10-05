using DeyeSolar.Web.Shared;

namespace DeyeSolar.Web.Tests;

public class AsyncQueryScopeTests
{
    [Fact]
    public async Task LateOldResponseCannotOverwriteNewSelectionOrReleaseItsLoadingState()
    {
        await using var scope = new AsyncQueryScope();
        var old = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var values = new List<int>();
        var finished = 0;
        CancellationToken oldToken = default;
        var first = scope.RunAsync(ct => { oldToken = ct; return old.Task; }, () => {}, values.Add, _ => Assert.Fail("No old error"), () => finished++);
        var second = scope.RunAsync(_ => current.Task, () => {}, values.Add, _ => Assert.Fail("No new error"), () => finished++);
        Assert.True(oldToken.IsCancellationRequested);
        old.SetResult(1); await first;
        Assert.Empty(values); Assert.True(scope.IsLoading); Assert.Equal(0, finished);
        current.SetResult(2); await second;
        Assert.Equal(new[] { 2 }, values); Assert.False(scope.IsLoading); Assert.Equal(1, finished);
    }

    [Fact]
    public async Task DisposedScopeFencesAReadThatIgnoresCancellation()
    {
        var scope = new AsyncQueryScope();
        var response = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0;
        var query = scope.RunAsync(_ => response.Task, () => {}, _ => callbacks++, _ => callbacks++, () => callbacks++);
        await scope.DisposeAsync();
        response.SetException(new InvalidOperationException("Late provider failure")); await query;
        await scope.RunAsync(_ => { Assert.Fail("No reads after disposal"); return Task.FromResult(0); }, () => {}, _ => {}, _ => {}, () => {});
        Assert.True(scope.IsDisposed); Assert.False(scope.IsLoading); Assert.Equal(0, callbacks);
    }

    [Fact]
    public async Task CurrentFailureFinishesAndAllowsTheNextRead()
    {
        await using var scope = new AsyncQueryScope();
        var errors = 0; var finished = 0; var result = 0;
        await scope.RunAsync<int>(_ => Task.FromException<int>(new InvalidOperationException()), () => {}, _ => {}, _ => errors++, () => finished++);
        Assert.Equal(1, errors); Assert.Equal(1, finished); Assert.False(scope.IsLoading);
        await scope.RunAsync(_ => Task.FromResult(42), () => {}, value => result = value, _ => Assert.Fail(), () => finished++);
        Assert.Equal(42, result); Assert.Equal(2, finished);
    }

    [Fact]
    public async Task DisposalStopsATimerEvenWhenTheRendererCallbackDoesNotComplete()
    {
        var clock = new ManualTimerClock();
        var scope = new AsyncQueryScope();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        scope.StartRefresh(clock, TimeSpan.FromMinutes(5), () => { calls++; started.SetResult(); return blocked.Task; });
        clock.Pulse(); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scope.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        clock.Pulse(); Assert.Equal(1, calls); Assert.True(clock.Timer!.Disposed);
        blocked.SetResult();
    }

    private sealed class ManualTimerClock : TimeProvider
    {
        public ManualTimer? Timer { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => Timer = new ManualTimer(callback, state);
        public void Pulse() => Timer?.Pulse();
    }
    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        public bool Disposed { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
        public void Pulse() { if (!Disposed) callback(state); }
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
