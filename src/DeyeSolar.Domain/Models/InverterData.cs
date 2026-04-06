namespace DeyeSolar.Domain.Models;

public record InverterData
{
    public int BatterySoc { get; init; }
    public double BatteryTemperature { get; init; }
    public double BatteryVoltage { get; init; }
    public int BatteryPower { get; init; }
    public double BatteryCurrent { get; init; }
    public int SolarProduction { get; init; }
    public int GridConsumption { get; init; }
    public int LoadPower { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
