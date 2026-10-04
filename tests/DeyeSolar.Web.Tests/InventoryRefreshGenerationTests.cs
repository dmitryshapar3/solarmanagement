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
    public async Task ObsoletePageRefreshCannotRepublishDevicesAfterIntegrationInvalidation(bool dashboard, bool replacement)
    {
        var snapshot = new DeviceStatusSnapshot();
        var oldDevice = new DevicePowerInfo(Guid.NewGuid().ToString("D"), "Retired account socket", "Socket", true, true, 850);
        var neighbor = new DevicePowerInfo(Guid.NewGuid().ToString("D"), "Current account neighbor", "Socket", true, false, 0);
        IReadOnlyList<DevicePowerInfo> authoritative = [neighbor];
        snapshot.Update([oldDevice]);
        var inventory = new HeldInventory();
        object page = dashboard ? new DeyeSolar.Web.Pages.Index() : new DeyeSolar.Web.Pages.Devices();
        Set(page, "DeviceSnapshot", snapshot);
        if (dashboard)
        {
            Set(page, "Snapshot", new InverterDataSnapshot());
            Set(page, "SocketInventory", inventory);
            Set(page, "RuleRepo", new EmptyRules());
        }
        else
        {
            Set(page, "SocketInventoryService", inventory);
            Set(page, "DeviceNames", new DeviceNameService(new Labels(), snapshot));
        }
        try
        {
            var pending = InvokeAsync(page, dashboard ? "RefreshManualDevicesAsync" : "RefreshDevices");
            await inventory.Started.Task;
            snapshot.Clear();
            if (replacement) snapshot.Update(authoritative);
            var lastUpdated = snapshot.LastUpdated;
            var publications = 0;
            snapshot.OnDataUpdated += () => ++publications;
            inventory.Complete.SetResult([oldDevice]);
            await pending;
            Assert.Equal(0, publications);
            Assert.Equal(lastUpdated, snapshot.LastUpdated);
            if (replacement) Assert.Same(authoritative, snapshot.Current);
            else Assert.Null(snapshot.Current);
            var rendered = Get(page, "_devices") as System.Collections.IEnumerable;
            var ids = rendered?.Cast<object>().Select(device => device switch
            {
                DevicePowerInfo reading => reading.Id,
                DeviceDto description => description.Id,
                _ => throw new InvalidDataException("Unexpected device representation.")
            }).ToArray() ?? [];
            Assert.Equal(replacement ? [neighbor.Id] : Array.Empty<string>(), ids);
        }
        finally { ((IDisposable)page).Dispose(); }
    }

    [Fact]
    public async Task DelayedLabelReadCannotRestoreDescriptionsOfAnInvalidatedInventory()
    {
        var snapshot = new DeviceStatusSnapshot();
        snapshot.Update([new(Guid.NewGuid().ToString("D"), "Retired socket", "Socket", true, true, 850)]);
        var labels = new HeldLabels();
        var page = new DeyeSolar.Web.Pages.Devices();
        Set(page, "DeviceSnapshot", snapshot);
        Set(page, "DeviceNames", new DeviceNameService(labels, snapshot));
        try
        {
            var descriptions = InvokeAsync(page, "LoadDescriptionsAsync");
            await labels.Started.Task;
            snapshot.Clear();
            labels.Complete.SetResult([]);
            await descriptions;
            Assert.Null(Get(page, "_devices"));
            Assert.Null(snapshot.Current);
        }
        finally { ((IDisposable)page).Dispose(); }
    }

    private static object? Get(object instance, string name) => instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance);
    private static void Set(object instance, string name, object value) => instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);
    private static Task InvokeAsync(object instance, string name) => (Task)instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, null)!;

    private sealed class HeldInventory : ISocketInventoryService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<DevicePowerInfo>> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<DevicePowerInfo>> RefreshDevicesAsync(CancellationToken ct)
        {
            Started.SetResult();
            return Complete.Task;
        }
        public Task<IReadOnlyList<DevicePowerInfo>> GetCachedDevicesAsync(CancellationToken ct) => throw new InvalidOperationException("Manual refresh must request current inventory.");
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
