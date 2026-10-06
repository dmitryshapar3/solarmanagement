using DeyeSolar.Web.Api;
using DeyeSolar.Web.Redesign;

namespace DeyeSolar.Web.Components.Ui;

public static class DevicePresentation
{
    public static DateTimeOffset? CurrentObservedStateSince(DeviceDto? device, DeviceHistoryDto? history)
    {
        if (device is not { Online: true, StateKnown: true } || history is null || history.Intervals.Count == 0)
            return null;
        var last = history.Intervals[^1];
        if (last.To != history.End || last.IsOn != device.IsOn || last.Evidence != "provider_observation")
            return null;
        var from = last.From;
        for (var index = history.Intervals.Count - 2; index >= 0; --index)
        {
            var previous = history.Intervals[index];
            if (previous.To != from || previous.IsOn != device.IsOn || previous.Evidence != "provider_observation")
                break;
            from = previous.From;
        }
        return from;
    }

    public static bool ManuallyPaused(DeviceDetailsDto? detail) => detail?.ControllingRules
        .Any(rule => !rule.Enabled && rule.PauseReason == "manual_override") == true;
}
