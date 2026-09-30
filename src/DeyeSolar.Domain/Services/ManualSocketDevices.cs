using DeyeSolar.Domain.Models;

namespace DeyeSolar.Domain.Services;

public static class ManualSocketDevices
{
    public static IReadOnlyList<DevicePowerInfo> Build(
        IEnumerable<TriggerRule> rules,
        string? shellyDeviceId,
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
                Online: true,
                IsOn: isOn,
                CurrentPowerW: null);
        }

        AddConfiguredDevice(devices, shellyDeviceId, SocketDeviceSources.Shelly, "Shelly configured socket", snapshotDevices);

        return devices.Values.ToList();
    }

    private static void AddConfiguredDevice(
        Dictionary<string, DevicePowerInfo> devices,
        string? rawId,
        string source,
        string name,
        IReadOnlyList<DevicePowerInfo>? snapshotDevices)
    {
        if (string.IsNullOrWhiteSpace(rawId))
            return;

        var entityId = SocketEntityIds.TryParse(rawId, out _, out _)
            ? rawId
            : SocketEntityIds.Create(source, rawId);

        if (devices.ContainsKey(entityId))
            return;

        // The configured device has no rule to carry its state, so overlay the
        // live snapshot when available instead of defaulting to OFF.
        var snapshot = snapshotDevices?.FirstOrDefault(d =>
            string.Equals(d.Id, entityId, StringComparison.OrdinalIgnoreCase));

        devices[entityId] = new DevicePowerInfo(
            entityId,
            name,
            FormatDeviceCategory(entityId),
            Online: snapshot?.Online ?? true,
            IsOn: snapshot?.IsOn ?? false,
            CurrentPowerW: snapshot?.CurrentPowerW);
    }

    private static string FormatDeviceName(string entityId)
        => SocketEntityIds.TryParse(entityId, out _, out var rawId) ? rawId : entityId;

    private static string FormatDeviceCategory(string entityId)
    {
        if (!SocketEntityIds.TryParse(entityId, out var source, out _))
            return "Configured";

        return source switch
        {
            SocketDeviceSources.Shelly => "Shelly",
            _ => "Configured"
        };
    }
}
