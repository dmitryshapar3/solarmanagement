using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Services;
using Xunit;

namespace DeyeSolar.Web.Tests;

public sealed class SnapshotGenerationTests
{
    [Fact]
    public void ClearedInverterCannotBeRepopulatedByAnEarlierReadAndCallbacksCanReenter()
    {
        var snapshot = new InverterDataSnapshot();
        var neighbour = new InverterDataSnapshot();
        var old = new InverterData { BatterySoc = 77 };
        neighbour.Update(old);
        var epoch = snapshot.Epoch;
        snapshot.Clear();
        Assert.False(snapshot.TryUpdate(old, epoch));
        Assert.Null(snapshot.Current);
        Assert.Same(old, neighbour.Current);
        snapshot.OnDataUpdated += () => { _ = snapshot.Current; };
        var fresh = old with { BatterySoc = 80 };
        Assert.True(snapshot.TryUpdate(fresh, snapshot.Epoch));
        Assert.Same(fresh, snapshot.Current);
    }

    [Fact]
    public void ClearedSocketInventoryRejectsOldGenerationAndKeepsNeighbourState()
    {
        var snapshot = new DeviceStatusSnapshot();
        var neighbour = new DeviceStatusSnapshot();
        DevicePowerInfo[] old = [new("registered", "Socket", "Socket", true, true, 100)];
        neighbour.Update(old);
        var epoch = snapshot.Epoch;
        snapshot.Clear();
        Assert.False(snapshot.TryUpdate(old, epoch));
        Assert.Null(snapshot.Current);
        Assert.Null(snapshot.LastUpdated);
        Assert.Same(old, neighbour.Current);
        snapshot.OnDataUpdated += () => { _ = snapshot.Epoch; };
        Assert.True(snapshot.TryUpdate([old[0] with { IsOn = false }], snapshot.Epoch));
        Assert.False(Assert.Single(snapshot.Current!).IsOn);
        Assert.True(Assert.Single(neighbour.Current!).IsOn);
    }
}
