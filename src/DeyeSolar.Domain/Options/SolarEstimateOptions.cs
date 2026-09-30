namespace DeyeSolar.Domain.Options;

/// <summary>Provisional site/module assumptions, independent of measured generation.</summary>
public sealed class SolarEstimateOptions
{
    public const string Section = "SolarEstimate";
    public double Latitude { get; set; } = 50.095278;
    public double Longitude { get; set; } = 20.070278;
    public string TimeZoneId { get; set; } = "Europe/Warsaw";
    public string LocationLabel { get; set; } = "Geodetów 17E, Kraków";
    // Eight garden-facing and seven road-facing modules share the existing 8.1 kWp total.
    public double Roof1Kwp { get; set; } = 4.32;
    public double Roof2Kwp { get; set; } = 3.78;
    public double Roof1Tilt { get; set; } = 25;
    public double Roof2Tilt { get; set; } = 25;
    // Approximate compass bearings inferred from the user's satellite screenshot, not a site survey.
    public double Roof1Azimuth { get; set; } = 230;
    public double Roof2Azimuth { get; set; } = 50;
    // LONGi LR7-60HVH-540M datasheet Pmax coefficient: -0.26% per degree Celsius.
    public double TemperatureCoefficient { get; set; } = -0.0026;
    // Use the fixed nominal instead of generic module spread; this is not a zero-uncertainty certification.
    public double TemperatureCoefficientUncertainty { get; set; } = 0;
    public double DcLossFraction { get; set; } = 0.06;
    public double MinimumDcLossFraction { get; set; } = 0.03;
    public double MaximumDcLossFraction { get; set; } = 0.12;
    public double FaimanU0 { get; set; } = 25;
    public double FaimanU1 { get; set; } = 6.84;
    public double WindAtModuleFactor { get; set; } = 0.5;
    public double CellTemperatureRiseAt1000 { get; set; } = 3;
    public double CellTemperatureUncertaintyC { get; set; } = 12;
    public double ConfigurationUncertaintyFraction { get; set; } = 0.08;
    public double SatelliteUncertaintyFraction { get; set; } = 0.15;
    public double ModelUncertaintyFraction { get; set; } = 0.25;
    public int NowcastStaleAfterMinutes { get; set; } = 20;
    public int NowcastMaximumAgeMinutes { get; set; } = 30;
    public int NowcastActualMaximumAgeMinutes { get; set; } = 10;
    public int StaleAfterMinutes { get; set; } = 45;
    public int MaximumAgeMinutes { get; set; } = 180;
    public int AlignmentToleranceSeconds { get; set; } = 120;
    public double NearZeroKw { get; set; } = 0.1;
    // Enable only after checking the device's measurePoints / PV inputs. Export or hybrid AC is not comparable.
    public bool DeyeSolarPowerIsPvDcConfirmed { get; set; }
    public string DeyeConfirmedDeviceSn { get; set; } = "";
    public string OperatingModeNote { get; set; } = "";
    public double InverterEfficiency { get; set; } = 0.97;
    public double? InverterAcLimitKw { get; set; }
    public string? ApiKey { get; set; }
    public double TotalKwp => Roof1Kwp + Roof2Kwp;

    public void Validate()
    {
        double[] numbers = [Latitude, Longitude, Roof1Kwp, Roof2Kwp, Roof1Tilt, Roof2Tilt,
            Roof1Azimuth, Roof2Azimuth, TemperatureCoefficient, TemperatureCoefficientUncertainty,
            DcLossFraction, MinimumDcLossFraction, MaximumDcLossFraction, FaimanU0, FaimanU1,
            WindAtModuleFactor, CellTemperatureRiseAt1000, CellTemperatureUncertaintyC,
            ConfigurationUncertaintyFraction, SatelliteUncertaintyFraction, ModelUncertaintyFraction, NearZeroKw, InverterEfficiency];
        if (numbers.Any(x => !double.IsFinite(x)) || Latitude is < -90 or > 90 || Longitude is < -180 or > 180
            || Roof1Kwp <= 0 || Roof2Kwp <= 0 || Roof1Tilt is < 0 or > 90 || Roof2Tilt is < 0 or > 90
            || Roof1Azimuth is < 0 or >= 360 || Roof2Azimuth is < 0 or >= 360
            || TemperatureCoefficient is < -0.02 or > 0 || TemperatureCoefficientUncertainty is < 0 or > 0.01
            || MinimumDcLossFraction < 0 || MaximumDcLossFraction >= 1
            || MinimumDcLossFraction > DcLossFraction || DcLossFraction > MaximumDcLossFraction
            || FaimanU0 <= 0 || FaimanU1 < 0 || WindAtModuleFactor is < 0 or > 1
            || CellTemperatureRiseAt1000 < 0 || CellTemperatureUncertaintyC < 0
            || ConfigurationUncertaintyFraction is < 0 or > 1 || SatelliteUncertaintyFraction is < 0 or > 1
            || ModelUncertaintyFraction is < 0 or > 1 || NowcastStaleAfterMinutes is < 10 or > 30
            || NowcastMaximumAgeMinutes <= NowcastStaleAfterMinutes || NowcastMaximumAgeMinutes > 60
            || NowcastActualMaximumAgeMinutes is < 1 or > 15
            || StaleAfterMinutes < 20 || MaximumAgeMinutes <= StaleAfterMinutes
            || AlignmentToleranceSeconds is < 0 or > 300 || NearZeroKw <= 0
            || InverterEfficiency is <= 0 or > 1
            || (InverterAcLimitKw.HasValue && (!double.IsFinite(InverterAcLimitKw.Value) || InverterAcLimitKw <= 0)))
            throw new ArgumentException("Invalid SolarEstimate configuration.");
        _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
    }
}
