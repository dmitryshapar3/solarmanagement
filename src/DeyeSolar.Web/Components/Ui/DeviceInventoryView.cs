using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Services;
using DeyeSolar.Web.Api;
using DeyeSolar.Web.Services;

namespace DeyeSolar.Web.Components.Ui;

/// <summary>Prevents a delayed discovery or label lookup from restoring an invalidated integration.</summary>
public static class DeviceInventoryView
{
    public sealed record View(long Epoch,IReadOnlyList<DeviceDto> Devices);
    public static async Task<IReadOnlyList<DeviceDto>> ReadAsync(DeviceStatusSnapshot snapshot,
        ISocketInventoryService inventory, DeviceNameService names, bool discover, CancellationToken ct)
        => (await ReadSnapshotAsync(snapshot,inventory,names,discover,ct)).Devices;

    public static async Task<View> ReadSnapshotAsync(DeviceStatusSnapshot snapshot,
        ISocketInventoryService inventory, DeviceNameService names, bool discover, CancellationToken ct)
    {
        var epoch = snapshot.Epoch;
        var current = snapshot.Current;
        if (current is null)
        {
            var fetched = discover ? await inventory.RefreshDevicesAsync(ct) : await inventory.GetCachedDevicesAsync(ct);
            ct.ThrowIfCancellationRequested();
            snapshot.TryUpdate(fetched, epoch);
            current = snapshot.Current;
        }
        while (current is not null)
        {
            epoch = snapshot.Epoch;
            var descriptions = await names.DescribeAsync(current, ct);
            ct.ThrowIfCancellationRequested();
            if (snapshot.Epoch == epoch && ReferenceEquals(snapshot.Current, current)) return new(epoch,descriptions);
            current = snapshot.Current;
        }
        return new(snapshot.Epoch,[]);
    }
}
