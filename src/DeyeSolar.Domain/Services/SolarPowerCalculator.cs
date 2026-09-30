using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;

namespace DeyeSolar.Domain.Services;

public static class SolarPowerCalculator
{
    public static double ToOpenMeteoAzimuth(double compassAzimuth)
    {
        if (!double.IsFinite(compassAzimuth)) throw new ArgumentOutOfRangeException(nameof(compassAzimuth));
        return ((compassAzimuth % 360 + 360) % 360) - 180;
    }

    public static SolarPowerEstimate Calculate(SolarRadiationObservation observation, SolarEstimateOptions options,
        DateTimeOffset now, SolarPowerBasis basis = SolarPowerBasis.PvDc)
    {
        options.Validate();
        if (basis == SolarPowerBasis.GridExport) throw new ArgumentException("Grid export is not PV generation.");
        if (observation.Timestamp > now || !ValidGti(observation.Roof1Gti) || !ValidGti(observation.Roof2Gti)
            || !double.IsFinite(observation.RecentVariabilityFraction) || observation.RecentVariabilityFraction < 0)
            throw new ArgumentException("Invalid or future radiation observation.");

        var weatherMissing = observation.AirTemperatureC is not (>= -80 and <= 65)
            || observation.WindSpeedMs is not (>= 0 and <= 100)
            || !observation.WeatherTimestamp.HasValue || observation.WeatherTimestamp > now
            || Math.Abs((observation.WeatherTimestamp.Value - observation.Timestamp).TotalMinutes) > 90;
        // Missing model weather is an explicit broad assumption, never an observation.
        var air = weatherMissing ? 20 : observation.AirTemperatureC!.Value;
        var wind = weatherMissing ? 1 : observation.WindSpeedMs!.Value * options.WindAtModuleFactor;
        var temperatureUncertainty = options.CellTemperatureUncertaintyC + (weatherMissing ? 20 : 0);
        var isModel = observation.Kind == SolarRadiationKind.WeatherModel;
        var ageMinutes = Math.Max(0, (now - (isModel ? observation.RetrievedAt ?? observation.Timestamp : observation.Timestamp)).TotalMinutes);
        // Engineering envelope, not a calibrated confidence interval. Clouds are already in GTI.
        var radiationUncertainty = Math.Clamp((isModel ? options.ModelUncertaintyFraction : options.SatelliteUncertaintyFraction)
            + options.ConfigurationUncertaintyFraction + 0.003 * ageMinutes
            + Math.Min(0.4, observation.RecentVariabilityFraction * 0.5), 0, 0.95);

        RoofPowerEstimate Roof(string name, double capacity, double tilt, double azimuth, double gti)
        {
            double Cell(double irradiance) => air + irradiance / (options.FaimanU0 + options.FaimanU1 * wind)
                + options.CellTemperatureRiseAt1000 * irradiance / 1000;
            double Power(double irradiance, double cell, double coefficient, double loss) =>
                capacity * irradiance / 1000 * Math.Max(0, 1 + coefficient * (cell - 25)) * (1 - loss);
            var cell = Cell(gti);
            var central = Power(gti, cell, options.TemperatureCoefficient, options.DcLossFraction);
            var candidates = new List<double> { central };
            foreach (var irradiance in new[] { gti * (1 - radiationUncertainty), gti * (1 + radiationUncertainty) })
            foreach (var cellDelta in new[] { -temperatureUncertainty, temperatureUncertainty })
            foreach (var coefficient in new[] { options.TemperatureCoefficient - options.TemperatureCoefficientUncertainty,
                         Math.Min(0, options.TemperatureCoefficient + options.TemperatureCoefficientUncertainty) })
            foreach (var loss in new[] { options.MinimumDcLossFraction, options.MaximumDcLossFraction })
                candidates.Add(Power(irradiance, Cell(irradiance) + cellDelta, coefficient, loss));
            return new(name, capacity, tilt, azimuth, gti, cell, central, candidates.Min(), candidates.Max());
        }

        var roofs = new[] { Roof("Southwest", options.Roof1Kwp, options.Roof1Tilt, options.Roof1Azimuth, observation.Roof1Gti),
            Roof("Northeast", options.Roof2Kwp, options.Roof2Tilt, options.Roof2Azimuth, observation.Roof2Gti) };
        double Convert(double dc) => basis == SolarPowerBasis.PvDc ? dc : ToAc(dc, options);
        return new(observation.Timestamp, now, basis, Convert(roofs.Sum(r => r.CentralKw)),
            Convert(roofs.Sum(r => r.LowerKw)), Convert(roofs.Sum(r => r.UpperKw)), options.TotalKwp,
            radiationUncertainty, weatherMissing, observation, roofs);
    }

    public static double ToAc(double dcKw, SolarEstimateOptions options)
    {
        options.Validate();
        if (!double.IsFinite(dcKw) || dcKw < 0) throw new ArgumentOutOfRangeException(nameof(dcKw));
        if (!options.InverterAcLimitKw.HasValue) throw new ArgumentException("AC estimate requires a known inverter limit.");
        return Math.Min(dcKw * options.InverterEfficiency, options.InverterAcLimitKw.Value);
    }

    public static SolarComparison Compare(SolarPowerEstimate estimate, SolarActual? actual,
        SolarEstimateOptions options, DateTimeOffset now, bool refreshFailed = false)
    {
        SolarComparison Missing(string reason) => new(SolarComparisonStatus.InsufficientData, actual, null, null, reason);
        var isModel = estimate.Observation.Kind == SolarRadiationKind.WeatherModel;
        var freshnessTime = isModel ? estimate.Observation.RetrievedAt ?? estimate.Timestamp : estimate.Timestamp;
        var staleAfter = isModel ? options.NowcastStaleAfterMinutes : options.StaleAfterMinutes;
        if (estimate.Timestamp > now || freshnessTime > now || (now - freshnessTime).TotalMinutes > staleAfter || refreshFailed)
            return Missing("Weather data is stale or could not be refreshed.");
        if (actual is null || !double.IsFinite(actual.PowerKw) || actual.PowerKw < 0 || actual.Timestamp > now)
            return Missing("No reliable Deye reading is available for the estimate time.");
        if (isModel && (now - actual.Timestamp).TotalMinutes > options.NowcastActualMaximumAgeMinutes)
            return Missing("The latest Deye reading is stale.");
        if (actual.Basis != estimate.Basis || actual.Basis == SolarPowerBasis.GridExport)
            return Missing("The power values use incompatible measurement types.");
        if (Math.Abs((actual.Timestamp - estimate.Timestamp).TotalSeconds) > options.AlignmentToleranceSeconds)
            return Missing("The measured power and weather estimate timestamps do not align.");
        var delta = actual.PowerKw - estimate.CentralKw;
        if (estimate.CentralKw < options.NearZeroKw)
            return new(SolarComparisonStatus.InsufficientData, actual, delta, null, "It is night or power is too low for a percentage comparison.");
        var status = actual.PowerKw < estimate.LowerKw ? SolarComparisonStatus.BelowEstimate
            : actual.PowerKw > estimate.UpperKw ? SolarComparisonStatus.AboveEstimate : SolarComparisonStatus.WithinRange;
        return new(status, actual, delta, delta / estimate.CentralKw * 100, null);
    }

    private static bool ValidGti(double value) => double.IsFinite(value) && value is >= 0 and <= 2000;
}
