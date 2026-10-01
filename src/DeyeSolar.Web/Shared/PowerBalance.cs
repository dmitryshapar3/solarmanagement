using DeyeSolar.Domain.Models;

namespace DeyeSolar.Web.Shared;

public sealed record PowerBalanceReading(double? Watts, string? UnavailableReason);

public static class PowerBalance
{
    // Signed grid import and battery discharge supply power. Export and charging consume it.
    public static double? Calculate(double? solar, double? grid, double? battery, double? load)
    {
        if (!solar.HasValue || !grid.HasValue || !battery.HasValue || !load.HasValue
            || !double.IsFinite(solar.Value) || !double.IsFinite(grid.Value)
            || !double.IsFinite(battery.Value) || !double.IsFinite(load.Value)
            || solar < 0 || load < 0) return null;
        var difference = solar.Value + grid.Value + battery.Value - load.Value;
        return double.IsFinite(difference) ? difference : null;
    }

    public static PowerBalanceReading FromReading(InverterData? reading, DateTimeOffset now,
        int alignmentToleranceSeconds = 120, int maximumAgeMinutes = 10, string? expectedDeviceSn = null)
    {
        if (reading is null || Calculate(reading.SolarProduction, reading.GridConsumption, reading.BatteryPower, reading.LoadPower) is not { } watts)
            return new(null, "Required power readings are unavailable.");
        if (reading.SolarObservedAt is not { } solarAt || reading.GridObservedAt is not { } gridAt
            || string.IsNullOrWhiteSpace(reading.SolarDeviceSn) || reading.SolarDeviceSn != reading.GridDeviceSn
            || (!string.IsNullOrWhiteSpace(expectedDeviceSn) && reading.SolarDeviceSn != expectedDeviceSn))
            return new(null, "Valid solar and grid measurements from the same inverter are required.");
        if (solarAt > now || gridAt > now || solarAt > reading.Timestamp || gridAt > reading.Timestamp
            || maximumAgeMinutes < 0 || (now - solarAt).TotalMinutes > maximumAgeMinutes || (now - gridAt).TotalMinutes > maximumAgeMinutes)
            return new(null, "Recent solar and grid measurements are required to calculate the balance.");
        if (alignmentToleranceSeconds < 0 || Math.Abs((solarAt - gridAt).TotalSeconds) > alignmentToleranceSeconds)
            return new(null, "Solar and grid measurement times are too far apart to calculate the balance.");
        return new(watts, null);
    }

    public static string BatteryLabel(int? watts) => watts is null ? "Battery power" : watts < 0 ? "Battery charging" : watts > 0 ? "Battery discharging" : "Battery idle";
    public static string Direction(double watts) => watts > 0 ? "Reported supply exceeds consumption" : watts < 0 ? "Reported consumption exceeds supply" : "Reported supply and consumption match";
}
