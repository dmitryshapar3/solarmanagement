using System.Reflection;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Services;
using DeyeSolar.Web.Api;
using DeyeSolar.Web.Services;

namespace DeyeSolar.Web.Tests;

public class InventoryRefreshGenerationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ObsoleteInitialInventoryReadCannotRepublishAfterIntegrationInvalidation(bool discover, bool replacement)
    {
        var snapshot = new DeviceStatusSnapshot();
        var oldDevice = new DevicePowerInfo(Guid.NewGuid().ToString("D"), "Retired account socket", "Socket", true, true, 850);
        var neighbor = new DevicePowerInfo(Guid.NewGuid().ToString("D"), "Current account neighbor", "Socket", true, false, 0);
        IReadOnlyList<DevicePowerInfo> authoritative = [neighbor];
        var inventory = new HeldInventory();
        var pending = DeyeSolar.Web.Components.Ui.DeviceInventoryView.ReadAsync(snapshot, inventory,
            new DeviceNameService(new Labels(), snapshot), discover, CancellationToken.None);
        await inventory.Started.Task;
        snapshot.Clear();
        if (replacement) snapshot.Update(authoritative);
        var lastUpdated = snapshot.LastUpdated;
        var publications = 0;
        snapshot.OnDataUpdated += () => ++publications;
        inventory.Complete.SetResult([oldDevice]);
        var descriptions = await pending;
        Assert.Equal(0, publications);
        Assert.Equal(lastUpdated, snapshot.LastUpdated);
        Assert.Equal(replacement ? [neighbor.Id] : Array.Empty<string>(), descriptions.Select(d => d.Id));
        if (replacement) Assert.Same(authoritative, snapshot.Current);
        else Assert.Null(snapshot.Current);
    }

    [Fact]
    public async Task DelayedLabelReadCannotRestoreDescriptionsOfAnInvalidatedInventory()
    {
        var snapshot = new DeviceStatusSnapshot();
        snapshot.Update([new(Guid.NewGuid().ToString("D"), "Retired socket", "Socket", true, true, 850)]);
        var labels = new HeldLabels();
        var pending = DeyeSolar.Web.Components.Ui.DeviceInventoryView.ReadAsync(snapshot, new HeldInventory(),
            new DeviceNameService(labels, snapshot), false, CancellationToken.None);
        await labels.Started.Task;
        snapshot.Clear();
        labels.Complete.SetResult([]);
        Assert.Empty(await pending);
        Assert.Null(snapshot.Current);
    }

    private sealed class HeldInventory : ISocketInventoryService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<DevicePowerInfo>> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<DevicePowerInfo>> RefreshDevicesAsync(CancellationToken ct)
        {
            Started.SetResult();
            return Complete.Task;
        }
        public Task<IReadOnlyList<DevicePowerInfo>> GetCachedDevicesAsync(CancellationToken ct) => RefreshDevicesAsync(ct);
    }
    private sealed class Labels : IDeviceLabelStore
    {
        public Task<Dictionary<string, string>> LoadAsync(CancellationToken ct) => Task.FromResult(new Dictionary<string, string>());
        public Task SaveAsync(Dictionary<string, string> labels, CancellationToken ct) => throw new InvalidOperationException("Inventory refresh cannot change labels.");
    }
    private sealed class HeldLabels : IDeviceLabelStore
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Dictionary<string, string>> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Dictionary<string, string>> LoadAsync(CancellationToken ct) { Started.SetResult(); return Complete.Task; }
        public Task SaveAsync(Dictionary<string, string> labels, CancellationToken ct) => throw new InvalidOperationException("Describing devices cannot change labels.");
    }
    private sealed class EmptyRules : IRuleRepository
    {
        public Task<List<TriggerRule>> GetAllAsync(CancellationToken ct) => Task.FromResult(new List<TriggerRule>());
        public Task<TriggerRule?> GetByIdAsync(int id, CancellationToken ct) => throw new NotSupportedException();
        public Task<TriggerRule> CreateAsync(TriggerRule rule, CancellationToken ct) => throw new NotSupportedException();
        public Task UpdateAsync(TriggerRule rule, CancellationToken ct) => throw new NotSupportedException();
        public Task RecordEvaluationAsync(int ruleId, DateTime when, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(int id, string configurationVersion, CancellationToken ct) => throw new NotSupportedException();
    }
}
