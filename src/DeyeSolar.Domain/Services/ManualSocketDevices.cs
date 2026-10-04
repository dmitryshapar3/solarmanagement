using DeyeSolar.Domain.Models;

namespace DeyeSolar.Domain.Services;

public static class ManualSocketDevices
{
    public static IReadOnlyList<DevicePowerInfo> Build(
        IEnumerable<TriggerRule> rules,
        string? configuredDeviceId,
        IReadOnlyList<DevicePowerInfo>? snapshotDevices = null)
    {
        var devices = new Dictionary<string, DevicePowerInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in rules
            .Where(r => !string.IsNullOrWhiteSpace(r.EntityId))
            .GroupBy(r => r.EntityId, StringComparer.OrdinalIgnoreCase))
        {
            var names = string.Join(", ", group.Select(r => r.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct());
            var isOn = group.Any(r => r.CurrentState);
            devices[group.Key] = new DevicePowerInfo(
                group.Key,
                string.IsNullOrWhiteSpace(names) ? FormatDeviceName(group.Key) : names,
                FormatDeviceCategory(group.Key),
                Online: false,
                IsOn: isOn,
                CurrentPowerW: null);
        }

        foreach (var snapshot in snapshotDevices ?? []) devices[snapshot.Id] = snapshot;
        if (!string.IsNullOrWhiteSpace(configuredDeviceId) && !devices.ContainsKey(configuredDeviceId))
            devices[configuredDeviceId] = new(configuredDeviceId, "Configured socket", "Socket", false, false, null);

        return devices.Values.ToList();
    }

    private static string FormatDeviceName(string entityId)
        => SocketEntityIds.TryParse(entityId, out _, out var rawId) ? rawId : entityId;

    private static string FormatDeviceCategory(string entityId) => "Socket";
}
