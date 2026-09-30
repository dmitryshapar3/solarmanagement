namespace DeyeSolar.Domain.Models;

public record InverterData
{
    public int BatterySoc { get; init; }
    public double BatteryTemperature { get; init; }
    public double BatteryVoltage { get; init; }
    public int BatteryPower { get; init; }
    public double BatteryCurrent { get; init; }
    public int SolarProduction { get; init; }
    // Deye's measurement time, present only for a valid PV reading. Timestamp remains the poll time.
    public DateTimeOffset? SolarObservedAt { get; init; }
    // Device provenance is required before historical readings can be compared with this installation.
    public string? SolarDeviceSn { get; init; }
    public int GridConsumption { get; init; }
    // Grid provenance is independent of whether the same response contains valid PV data.
    public DateTimeOffset? GridObservedAt { get; init; }
    public string? GridDeviceSn { get; init; }
    public int LoadPower { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
