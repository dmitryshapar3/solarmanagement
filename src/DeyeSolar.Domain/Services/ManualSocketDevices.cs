using DeyeSolar.Domain.Models;

namespace DeyeSolar.Domain.Services;

public static class ManualSocketDevices
{
    public static IReadOnlyList<DevicePowerInfo> Build(
        IEnumerable<TriggerRule> rules,
        IReadOnlyList<DevicePowerInfo>? snapshotDevices = null)
    {
        var devices = new Dictionary<string, DevicePowerInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in rules
            .Where(r => Guid.TryParse(r.EntityId, out _))
            .GroupBy(r => Guid.Parse(r.EntityId).ToString("D"), StringComparer.OrdinalIgnoreCase))
        {
            var names = string.Join(", ", group.Select(r => r.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct());
            var isOn = group.Any(r => r.CurrentState);
            devices[group.Key] = new DevicePowerInfo(
                group.Key,
                string.IsNullOrWhiteSpace(names) ? group.Key : names,
                "Socket",
                Online: false,
                IsOn: isOn,
                CurrentPowerW: null);
        }

        foreach (var snapshot in snapshotDevices ?? []) devices[snapshot.Id] = snapshot;
        return devices.Values.ToList();
    }
}
