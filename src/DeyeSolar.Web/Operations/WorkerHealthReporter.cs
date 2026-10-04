using System.Collections.Concurrent;

namespace DeyeSolar.Web.Operations;

public interface IWorkerHealthReporter
{
    void Started(string name, TimeSpan maximumSilence);
    void Succeeded(string name);
    void Failed(string name);
    void Stopped(string name);
}

public sealed class WorkerHealthReporter(TimeProvider clock) : IWorkerHealthReporter
{
    private sealed record State(DateTimeOffset UpdatedAt, TimeSpan MaximumSilence, bool Failed);
    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.Ordinal);
    public void Started(string name, TimeSpan maximumSilence)
    {
        if (string.IsNullOrWhiteSpace(name) || maximumSilence <= TimeSpan.Zero) throw new ArgumentException("A worker needs a name and positive heartbeat deadline.");
        _states[name] = new(clock.GetUtcNow(), maximumSilence, false);
    }
    public void Succeeded(string name) => _states.AddOrUpdate(name, _ => throw new InvalidOperationException("Start the worker before reporting."),
        (_, state) => state with { UpdatedAt = clock.GetUtcNow(), Failed = false });
    public void Failed(string name) => _states.AddOrUpdate(name, _ => throw new InvalidOperationException("Start the worker before reporting."),
        (_, state) => state with { UpdatedAt = clock.GetUtcNow(), Failed = true });
    public void Stopped(string name) => _states.TryRemove(name, out _);
    public bool IsHealthy => _states.Values.All(state => !state.Failed && clock.GetUtcNow() - state.UpdatedAt <= state.MaximumSilence);
}
