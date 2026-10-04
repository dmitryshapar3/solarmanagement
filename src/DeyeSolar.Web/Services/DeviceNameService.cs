using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Services;
using DeyeSolar.Web.Api;
using DeyeSolar.Web.Data;

namespace DeyeSolar.Web.Services;

public sealed record DeviceNameRequest(string? Name);
public interface IDeviceLabelStore
{
    Task<Dictionary<string, string>> LoadAsync(CancellationToken ct);
    Task SaveAsync(Dictionary<string, string> labels, CancellationToken ct);
}

/// <summary>Uses existing settings storage, so installation isolation applies through AppSettingsService.</summary>
public sealed class AppSettingsDeviceLabelStore(AppSettingsService settings) : IDeviceLabelStore
{
    public sealed class DeviceLabelsOptions { public string LabelsJson { get; set; } = "{}"; }
    public async Task<Dictionary<string, string>> LoadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var value = await settings.LoadSectionAsync<DeviceLabelsOptions>("DeviceLabels");
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(value.LabelsJson) ?? []; }
        catch (JsonException) { return []; }
    }
    public Task SaveAsync(Dictionary<string, string> labels, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return settings.SaveSectionAsync("DeviceLabels", new DeviceLabelsOptions { LabelsJson = JsonSerializer.Serialize(labels) });
    }
}

/// <summary>Local display labels never change the cloud device, ID, rules or switch state.</summary>
public sealed class DeviceNameService(IDeviceLabelStore store, DeviceStatusSnapshot devices)
{
    private static readonly SemaphoreSlim Write = new(1, 1);
    public static string CanonicalId(string id) => Guid.TryParse(id, out var key) ? key.ToString("D")
        : SocketEntityIds.RawIdOrSelf(id.Trim()).ToLowerInvariant();
    public static string LabelKey(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        (Guid.TryParse(id, out _) ? "socket:" : "shelly:") + CanonicalId(id))));
    public static bool TryName(string? value, out string? name)
    {
        name = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return name is null || name.Length <= 80 && !name.Any(char.IsControl);
    }
    public async Task<IReadOnlyList<DeviceDto>> DescribeAsync(IReadOnlyList<DevicePowerInfo> source, CancellationToken ct)
    {
        var labels = await store.LoadAsync(ct);
        return source.Select(device => Describe(device, labels)).ToArray();
    }
    public async Task<DeviceDto?> RenameAsync(string id, string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(char.IsControl)) return null;
        if (!TryName(name, out var normalized)) throw new ArgumentException("Use a device name of up to 80 characters without control characters.");
        var device = devices.Current?.FirstOrDefault(device => CanonicalId(device.Id) == CanonicalId(id));
        // Discovery establishes membership. A caller cannot add an arbitrary cloud ID by naming it.
        if (device is null) return null;
        await Write.WaitAsync(ct);
        try
        {
            var labels = await store.LoadAsync(ct);
            if (normalized is null) labels.Remove(LabelKey(device.Id));
            else labels[LabelKey(device.Id)] = normalized;
            await store.SaveAsync(labels, ct);
            return Describe(device, labels);
        }
        finally { Write.Release(); }
    }
    private static DeviceDto Describe(DevicePowerInfo device, IReadOnlyDictionary<string, string> labels)
    {
        labels.TryGetValue(LabelKey(device.Id), out var name);
        if (!TryName(name, out name)) name = null;
        return new(device.Id, name ?? device.Name, device.Category, device.Online, device.IsOn, device.CurrentPowerW,
            CloudName: device.Name, LocalName: name);
    }
}
