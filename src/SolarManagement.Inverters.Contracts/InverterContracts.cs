namespace SolarManagement.Inverters.Contracts;

public readonly record struct InverterId(Guid Value);
public readonly record struct Watts(int Value);
public readonly record struct Percent(decimal Value);
public readonly record struct Celsius(double Value);
public readonly record struct Volts(double Value);
public readonly record struct Amperes(double Value);

public enum MeasurementQuality { Good, Missing, Invalid, Stale }
public enum SolarPowerBasis { Unknown, PvDc, InverterAcOutput }

public sealed record Measurement<T>(T? Value, DateTimeOffset? ObservedAt,
    MeasurementQuality Quality) where T : struct;

public sealed record InverterCapabilities(bool HasBattery, bool HasSolarPower,
    bool HasSignedGridPower, bool HasLoadPower, bool HasGridPowerHistory,
    SolarPowerBasis SolarBasis);

public interface IInverterTelemetry
{
    InverterId DeviceId { get; }
    DateTimeOffset ReceivedAt { get; }
    Measurement<Percent> BatterySoc { get; }
    Measurement<Watts> BatteryPower { get; }
    Measurement<Celsius> BatteryTemperature { get; }
    Measurement<Volts> BatteryVoltage { get; }
    Measurement<Amperes> BatteryCurrent { get; }
    Measurement<Watts> SolarPower { get; }
    Measurement<Watts> GridPower { get; }
    Measurement<Watts> LoadPower { get; }
    SolarPowerBasis SolarBasis { get; }
}

public sealed record InverterTelemetry(InverterId DeviceId, DateTimeOffset ReceivedAt,
    Measurement<Percent> BatterySoc, Measurement<Watts> BatteryPower,
    Measurement<Celsius> BatteryTemperature, Measurement<Volts> BatteryVoltage,
    Measurement<Amperes> BatteryCurrent, Measurement<Watts> SolarPower,
    Measurement<Watts> GridPower, Measurement<Watts> LoadPower,
    SolarPowerBasis SolarBasis) : IInverterTelemetry;

public sealed record InverterDescriptor(InverterId Id, string Name, InverterCapabilities Capabilities);
public sealed record GridHistoryQuery(DateTimeOffset FromInclusive, DateTimeOffset ToExclusive,
    string? ContinuationToken = null);
public sealed record GridPowerSample(DateTimeOffset ObservedAt, Watts Power);
public sealed record GridHistoryPage(InverterId DeviceId, IReadOnlyList<GridPowerSample> Samples,
    string? ContinuationToken, bool IsComplete);

public interface IInverter
{
    InverterId Id { get; }
    InverterCapabilities Capabilities { get; }
    Task<IInverterTelemetry> ReadAsync(CancellationToken ct);
}

public interface IInverterGridHistory
{
    Task<GridHistoryPage> ReadGridHistoryAsync(GridHistoryQuery query, CancellationToken ct);
}

public interface IInverterCatalog
{
    Task<IReadOnlyList<InverterDescriptor>> ListRegisteredAsync(CancellationToken ct);
    Task<IInverter> GetAsync(InverterId id, CancellationToken ct);
    Task<IInverterGridHistory?> GetGridHistoryAsync(InverterId id, CancellationToken ct);
}
