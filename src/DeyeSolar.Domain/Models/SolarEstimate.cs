namespace DeyeSolar.Domain.Models;

public enum SolarPowerBasis { PvDc, Ac, GridExport }
public enum SolarComparisonStatus { InsufficientData, WithinRange, BelowEstimate, AboveEstimate }
public enum SolarRadiationKind { Satellite, WeatherModel }

public sealed record SolarWeatherSample(DateTimeOffset Timestamp, double Roof1Gti, double Roof2Gti,
    double? AirTemperatureC, double? WindSpeedMs, double? CloudCoverPercent);

public sealed record SolarRadiationObservation(
    DateTimeOffset Timestamp, double Roof1Gti, double Roof2Gti,
    double? AirTemperatureC, double? WindSpeedMs, DateTimeOffset? WeatherTimestamp,
    double RecentVariabilityFraction)
{
    public SolarRadiationKind Kind { get; init; }
    public IReadOnlyList<SolarWeatherSample>? Forecast { get; init; }
    public DateTimeOffset? RetrievedAt { get; init; }
    public double? CloudCoverPercent { get; init; }
    public DateTimeOffset? ModelPeriodStart { get; init; }
    public DateTimeOffset? ModelPeriodEnd { get; init; }
}

public sealed record RoofPowerEstimate(string Name, double CapacityKwp, double Tilt, double CompassAzimuth,
    double Gti, double CellTemperatureC, double CentralKw, double LowerKw, double UpperKw);

public sealed record SolarPowerEstimate(DateTimeOffset Timestamp, DateTimeOffset CalculatedAt,
    SolarPowerBasis Basis, double CentralKw, double LowerKw, double UpperKw, double TotalKwp,
    double RadiationUncertaintyFraction, bool WeatherMissing, SolarRadiationObservation Observation,
    IReadOnlyList<RoofPowerEstimate> Roofs);

public sealed record SolarActual(DateTimeOffset Timestamp, double PowerKw, SolarPowerBasis Basis);

public sealed record SolarComparison(SolarComparisonStatus Status, SolarActual? Actual,
    double? DeviationKw, double? DeviationPercent, string? Reason);

public sealed record SolarEstimateState(SolarPowerEstimate? Estimate, SolarComparison Comparison,
    bool RefreshFailed, DateTimeOffset? LastSuccessAt, string? Error)
{
    public SolarPowerEstimate? ComparisonEstimate { get; init; }
    public static SolarEstimateState Empty { get; } = new(null,
        new(SolarComparisonStatus.InsufficientData, null, null, null, "Waiting for weather data to estimate current power."), false, null, null);
}
