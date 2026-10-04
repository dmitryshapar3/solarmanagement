namespace DeyeSolar.Domain.Models;

public record InverterData
{
    public SolarManagement.Inverters.Contracts.IInverterTelemetry? Telemetry { get; init; }
    public bool? BatteryPowerValid => Telemetry is null ? null : Telemetry.BatteryPower.Quality == SolarManagement.Inverters.Contracts.MeasurementQuality.Good;
    public bool? BatteryTemperatureValid => Telemetry is null ? null : Telemetry.BatteryTemperature.Quality == SolarManagement.Inverters.Contracts.MeasurementQuality.Good;
    public bool? BatteryVoltageValid => Telemetry is null ? null : Telemetry.BatteryVoltage.Quality == SolarManagement.Inverters.Contracts.MeasurementQuality.Good;
    public bool? BatteryCurrentValid => Telemetry is null ? null : Telemetry.BatteryCurrent.Quality == SolarManagement.Inverters.Contracts.MeasurementQuality.Good;
    public bool? LoadPowerValid => Telemetry is null ? null : Telemetry.LoadPower.Quality == SolarManagement.Inverters.Contracts.MeasurementQuality.Good;
    public bool? GridPowerValid => Telemetry is null ? null : Telemetry.GridPower.Quality == SolarManagement.Inverters.Contracts.MeasurementQuality.Good;
    public bool? SolarPowerValid => Telemetry is null ? null : Telemetry.SolarPower.Quality == SolarManagement.Inverters.Contracts.MeasurementQuality.Good;
    public Guid? InverterId { get; init; }
    public bool? BatterySocValid { get; init; }
    public long ConfigurationRevision { get; init; }
    public long RuntimeGeneration { get; init; }
    public int BatterySoc { get; init; }
    public double BatteryTemperature { get; init; }
    public double BatteryVoltage { get; init; }
    public int BatteryPower { get; init; }
    public double BatteryCurrent { get; init; }
    public int SolarProduction { get; init; }
    // Measurement time belongs to the source; Timestamp remains the retrieval time.
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
