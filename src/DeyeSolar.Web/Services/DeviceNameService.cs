using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Services;
using DeyeSolar.Web.Api;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Integrations;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Services;

public sealed record DeviceNameRequest(string? Name);
public interface IDeviceLabelStore
{
    Task<Dictionary<string, string>> LoadAsync(CancellationToken ct);
    Task SaveAsync(Dictionary<string, string> labels, CancellationToken ct);
}

/// <summary>Uses existing settings storage, so installation isolation applies through AppSettingsService.</summary>
public sealed class AppSettingsDeviceLabelStore(IAppSettingsReader settings, IAppSettingsWriter writer) : IDeviceLabelStore
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
        return writer.SaveSectionAsync("DeviceLabels", new DeviceLabelsOptions { LabelsJson = JsonSerializer.Serialize(labels) });
    }
}

/// <summary>Local display labels never change the cloud device, ID, rules or switch state.</summary>
public sealed class DeviceNameService(IDeviceLabelStore store, DeviceStatusSnapshot devices,
    IDbContextFactory<DeyeSolarDbContext>? factory = null, IntegrationChangeNotifier? changes = null)
{
    private static readonly SemaphoreSlim Write = new(1, 1);
    private static string LabelKey(Guid id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        "socket:" + id.ToString("D"))));
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
        if (!Guid.TryParse(id, out var deviceId)) return null;
        if (!TryName(name, out var normalized)) throw new ArgumentException("Use a device name of up to 80 characters without control characters.");
        var device = devices.Current?.FirstOrDefault(device => Guid.TryParse(device.Id, out var discoveredId) && discoveredId == deviceId);
        // Discovery establishes membership. A caller cannot add an arbitrary cloud ID by naming it.
        if (device is null) return null;
        await Write.WaitAsync(ct);
        try
        {
            var labels = await store.LoadAsync(ct);
            if (normalized is null)
            {
                // A label supplied while selecting a device has the same Restore semantics.
                // Keep the original provider name and all capability/automation metadata.
                if (factory is not null)
                {
                    await using var db = await factory.CreateDbContextAsync(ct);
                    var binding = await db.IntegrationDeviceBindings.SingleOrDefaultAsync(b => b.Id == deviceId, ct);
                    if (binding is not null && IntegrationDeviceDisplayName.Read(binding) is not null)
                    {
                        await using var transaction = await db.Database.BeginTransactionAsync(ct);
                        if (await IntegrationPersistenceGuard.LockInstanceAsync(db, binding.InstanceId, ct) is null)
                            return null;
                        await db.Entry(binding).ReloadAsync(ct);
                        binding.MetadataJson = IntegrationDeviceDisplayName.Write(binding, null);
                        await db.SaveChangesAsync(ct);
                        await transaction.CommitAsync(ct);
                        changes?.Publish(binding.InstallationId, binding.InstanceId);
                    }
                }
                labels.Remove(LabelKey(deviceId));
                device = device with { Name = device.CloudName ?? device.Name };
            }
            else labels[LabelKey(deviceId)] = normalized;
            await store.SaveAsync(labels, ct);
            return Describe(device, labels);
        }
        finally { Write.Release(); }
    }
    private static DeviceDto Describe(DevicePowerInfo device, IReadOnlyDictionary<string, string> labels)
    {
        string? name = null;
        if (Guid.TryParse(device.Id, out var deviceId)) labels.TryGetValue(LabelKey(deviceId), out name);
        if (!TryName(name, out name)) name = null;
        var cloudName = device.CloudName ?? device.Name;
        var hostName = device.CloudName is not null && device.Name != cloudName ? device.Name : null;
        return new(device.Id, name ?? device.Name, device.Category, device.Online, device.IsOn, device.CurrentPowerW,
            CloudName: cloudName, LocalName: name ?? hostName, StateKnown: device.StateKnown == true);
    }
}
