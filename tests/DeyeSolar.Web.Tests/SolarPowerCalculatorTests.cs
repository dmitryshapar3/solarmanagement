using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using DeyeSolar.Web.Shared;

namespace DeyeSolar.Web.Tests;

public class SolarPowerCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 11, 20, 0, TimeSpan.Zero);
    private static SolarRadiationObservation Observation(double sw = 800, double ne = 400) =>
        new(Now.AddMinutes(-20), sw, ne, 20, 2, Now.AddMinutes(-20), 0.1);

    [Theory]
    [InlineData(0, 5)]
    [InlineData(5, 0)]
    public void SinglePlaneInstallationKeepsTheEmptyPlaneAtZero(double firstCapacity, double secondCapacity)
    {
        var options = new SolarEstimateOptions { Roof1Kwp = firstCapacity, Roof2Kwp = secondCapacity };
        var estimate = SolarPowerCalculator.Calculate(Observation(), options, Now);
        var empty = estimate.Roofs.Single(roof => roof.CapacityKwp == 0);
        Assert.Equal(0, empty.CentralKw);
        Assert.Equal(0, empty.LowerKw);
        Assert.Equal(0, empty.UpperKw);
        Assert.True(double.IsFinite(estimate.CentralKw));
        Assert.True(estimate.CentralKw > 0);
        Assert.Equal(5, estimate.TotalKwp);
    }

    [Fact]
    public void EmptyNegativeAndOverflowingInstallationCapacityRemainInvalid()
    {
        foreach (var capacities in new[] { (0d, 0d), (-1d, 5d), (5d, -1d), (double.MaxValue, double.MaxValue) })
            Assert.Throws<ArgumentException>(() => new SolarEstimateOptions { Roof1Kwp = capacities.Item1, Roof2Kwp = capacities.Item2 }.Validate());
    }

    [Theory]
    [InlineData("weather", "Weather data is stale or could not be refreshed.")]
    [InlineData("missing", "No reliable Deye reading is available for the estimate time.")]
    [InlineData("stale", "The latest Deye reading is stale.")]
    [InlineData("basis", "The power values use incompatible measurement types.")]
    [InlineData("alignment", "The measured power and weather estimate timestamps do not align.")]
    [InlineData("night", "It is night or power is too low for a percentage comparison.")]
    public void SourceDetailsAndComparisonReasonsAreEnglish(string scenario, string expected)
    {
        var options = new SolarEstimateOptions();
        var observation = Observation() with { Timestamp = Now, WeatherTimestamp = Now,
            Kind = SolarRadiationKind.WeatherModel, RetrievedAt = Now };
        var estimate = SolarPowerCalculator.Calculate(observation, options, Now);
        SolarActual? actual = new(Now, estimate.CentralKw, SolarPowerBasis.PvDc);
        if (scenario == "missing") actual = null;
        if (scenario == "stale") actual = actual! with { Timestamp = Now.AddMinutes(-11) };
        if (scenario == "basis") actual = actual! with { Basis = SolarPowerBasis.Ac };
        if (scenario == "alignment") actual = actual! with { Timestamp = Now.AddMinutes(-3) };
        if (scenario == "night") estimate = estimate with { CentralKw = 0 };

        var comparison = SolarPowerCalculator.Compare(estimate, actual, options, Now, refreshFailed: scenario == "weather");

        Assert.Equal(SolarComparisonStatus.InsufficientData, comparison.Status);
        Assert.Equal(expected, comparison.Reason);
        Assert.Equal(new[] { "Southwest", "Northeast" }, estimate.Roofs.Select(roof => roof.Name));
        Assert.DoesNotMatch("[\\u0400-\\u04FF]", comparison.Reason!);
    }

    [Theory]
    [InlineData(219, 39)] [InlineData(39, -141)] [InlineData(0, -180)]
    [InlineData(90, -90)] [InlineData(180, 0)] [InlineData(270, 90)] [InlineData(360, -180)]
    [InlineData(230, 50)] [InlineData(50, -130)]
    public void CompassAzimuthUsesSouthZero(double compass, double expected) =>
        Assert.Equal(expected, SolarPowerCalculator.ToOpenMeteoAzimuth(compass));

    [Fact]
    public void CalculatesEachPlaneWithoutApplyingAdditionalCloudFactor()
    {
        var options = new SolarEstimateOptions { Roof1Kwp = 4.05, Roof2Kwp = 4.05,
            TemperatureCoefficient = 0, DcLossFraction = 0, MinimumDcLossFraction = 0 };
        var estimate = SolarPowerCalculator.Calculate(Observation(), options, Now);
        Assert.Equal(4.86, estimate.CentralKw, 8);
        Assert.Equal(3.24, estimate.Roofs[0].CentralKw, 8);
        Assert.Equal(1.62, estimate.Roofs[1].CentralKw, 8);
        Assert.Equal(estimate.CentralKw, estimate.Roofs.Sum(r => r.CentralKw));
        Assert.Equal(SolarPowerBasis.PvDc, estimate.Basis);
        Assert.True(estimate.LowerKw < estimate.CentralKw && estimate.UpperKw > estimate.CentralKw);
    }

    [Fact]
    public void FaimanUsesWattsPerSquareMetreMetresPerSecondAndCellTemperature()
    {
        var options = new SolarEstimateOptions { Roof1Kwp = 4.05, Roof2Kwp = 4.05, TemperatureCoefficient = -0.004 };
        var estimate = SolarPowerCalculator.Calculate(Observation(1000, 0), options, Now);
        var cell = 20 + 1000 / (25 + 6.84 * 1) + 3;
        Assert.Equal(cell, estimate.Roofs[0].CellTemperatureC, 8);
        Assert.Equal(4.05 * (1 - 0.004 * (cell - 25)) * 0.94, estimate.CentralKw, 8);
        Assert.True(estimate.CentralKw < 4.05);
        var hotter = SolarPowerCalculator.Calculate(Observation(1000, 0) with { AirTemperatureC = 40 }, options, Now);
        Assert.True(hotter.CentralKw < estimate.CentralKw);
    }

    [Theory]
    [InlineData(5, 25, 0.54)]
    [InlineData(25, 45, 0.51192)]
    public void Confirmed540WModuleUsesItsNominalTemperatureCoefficient(double airTemperature, double cellTemperature, double expectedKw)
    {
        var options = OnePanelWithoutOtherLossesOrUncertainty();
        var observation = Observation(1000, 0) with
        {
            Timestamp = Now, WeatherTimestamp = Now, RetrievedAt = Now, Kind = SolarRadiationKind.WeatherModel,
            AirTemperatureC = airTemperature, RecentVariabilityFraction = 0
        };

        var estimate = SolarPowerCalculator.Calculate(observation, options, Now);

        Assert.Equal(cellTemperature, estimate.Roofs[0].CellTemperatureC, 10);
        Assert.Equal(expectedKw, estimate.CentralKw, 10);
        Assert.Equal(expectedKw, estimate.LowerKw, 10);
        Assert.Equal(expectedKw, estimate.UpperKw, 10);
        Assert.Equal(0, estimate.Roofs[1].CentralKw);
        Assert.Equal(0, estimate.Roofs[1].LowerKw);
        Assert.Equal(0, estimate.Roofs[1].UpperKw);
    }

    [Fact]
    public void ExplicitGenericModuleSettingsStillControlPowerAndRange()
    {
        var options = OnePanelWithoutOtherLossesOrUncertainty();
        options.TemperatureCoefficient = -0.004;
        options.TemperatureCoefficientUncertainty = 0.001;
        var observation = Observation(1000, 0) with
        {
            Timestamp = Now, WeatherTimestamp = Now, RetrievedAt = Now, Kind = SolarRadiationKind.WeatherModel,
            AirTemperatureC = 25, RecentVariabilityFraction = 0
        };

        var estimate = SolarPowerCalculator.Calculate(observation, options, Now);

        Assert.Equal(45, estimate.Roofs[0].CellTemperatureC, 10);
        Assert.Equal(0.4968, estimate.CentralKw, 10);
        Assert.Equal(0.486, estimate.LowerKw, 10);
        Assert.Equal(0.5076, estimate.UpperKw, 10);
        Assert.Equal(-0.004, options.TemperatureCoefficient);
        Assert.Equal(0.001, options.TemperatureCoefficientUncertainty);
    }

    [Theory]
    [InlineData(-40)]
    [InlineData(40)]
    public void ConfirmedModuleKeepsOrderedModelBoundsInColdAndHotWeather(double airTemperature)
    {
        var options = new SolarEstimateOptions();
        var observation = Observation(1000, 600) with
        {
            Timestamp = Now, WeatherTimestamp = Now, RetrievedAt = Now, Kind = SolarRadiationKind.WeatherModel,
            AirTemperatureC = airTemperature, RecentVariabilityFraction = 0
        };

        var estimate = SolarPowerCalculator.Calculate(observation, options, Now);

        Assert.False(estimate.WeatherMissing);
        Assert.True(double.IsFinite(estimate.LowerKw) && double.IsFinite(estimate.CentralKw) && double.IsFinite(estimate.UpperKw));
        Assert.True(0 <= estimate.LowerKw && estimate.LowerKw < estimate.CentralKw && estimate.CentralKw < estimate.UpperKw);
        Assert.All(estimate.Roofs, roof =>
        {
            Assert.True(0 <= roof.LowerKw && roof.LowerKw < roof.CentralKw && roof.CentralKw < roof.UpperKw);
            Assert.True(airTemperature < 0 ? roof.CellTemperatureC < 25 : roof.CellTemperatureC > 25);
        });
    }

    [Fact]
    public void UncertainWeatherAndRapidVariationProduceWiderEnvelope()
    {
        var options = new SolarEstimateOptions();
        var normal = SolarPowerCalculator.Calculate(Observation(), options, Now);
        var uncertain = SolarPowerCalculator.Calculate(Observation() with { AirTemperatureC = null, RecentVariabilityFraction = 1 }, options, Now);
        Assert.True(uncertain.WeatherMissing);
        Assert.True(uncertain.UpperKw - uncertain.LowerKw > normal.UpperKw - normal.LowerKw);
        var old = SolarPowerCalculator.Calculate(Observation(), options, Now.AddMinutes(60));
        Assert.True(old.RadiationUncertaintyFraction > normal.RadiationUncertaintyFraction);
    }

    [Fact]
    public void NightHasNoPercentageDivisionOrDefectStatus()
    {
        var options = new SolarEstimateOptions();
        var estimate = SolarPowerCalculator.Calculate(Observation(0, 0), options, Now);
        var comparison = SolarPowerCalculator.Compare(estimate, new(estimate.Timestamp, 0, SolarPowerBasis.PvDc), options, Now);
        Assert.Equal(0, estimate.CentralKw);
        Assert.Equal(0, estimate.UpperKw);
        Assert.Null(comparison.DeviationPercent);
        Assert.Equal(SolarComparisonStatus.InsufficientData, comparison.Status);
    }

    [Theory]
    [InlineData(SolarPowerBasis.Ac)] [InlineData(SolarPowerBasis.GridExport)]
    public void DoesNotComparePvDcWithAcOrExport(SolarPowerBasis basis)
    {
        var options = new SolarEstimateOptions();
        var estimate = SolarPowerCalculator.Calculate(Observation(), options, Now);
        var comparison = SolarPowerCalculator.Compare(estimate, new(estimate.Timestamp, 4, basis), options, Now);
        Assert.Equal(SolarComparisonStatus.InsufficientData, comparison.Status);
        Assert.Null(comparison.DeviationKw);
    }

    [Theory]
    [InlineData(0, false, false, SolarComparisonStatus.WithinRange)]
    [InlineData(121, false, false, SolarComparisonStatus.InsufficientData)]
    [InlineData(0, true, false, SolarComparisonStatus.InsufficientData)]
    [InlineData(0, false, true, SolarComparisonStatus.InsufficientData)]
    public void RequiresAlignedFreshSuccessfulMeasurements(int offsetSeconds, bool failed, bool stale, SolarComparisonStatus expected)
    {
        var options = new SolarEstimateOptions();
        var estimate = SolarPowerCalculator.Calculate(Observation(), options, Now);
        var comparison = SolarPowerCalculator.Compare(estimate,
            new(estimate.Timestamp.AddSeconds(offsetSeconds), estimate.CentralKw, SolarPowerBasis.PvDc), options,
            stale ? Now.AddHours(1) : Now, failed);
        Assert.Equal(expected, comparison.Status);
        if (expected == SolarComparisonStatus.InsufficientData) Assert.Null(comparison.DeviationPercent);
        else Assert.Equal(0, comparison.DeviationPercent);
    }

    [Fact]
    public void RangeStatusesAndPercentUseExpectedPowerAsDenominator()
    {
        var options = new SolarEstimateOptions();
        var estimate = SolarPowerCalculator.Calculate(Observation(), options, Now);
        var below = SolarPowerCalculator.Compare(estimate, new(estimate.Timestamp, 0, SolarPowerBasis.PvDc), options, Now);
        Assert.Equal(SolarComparisonStatus.BelowEstimate, below.Status);
        Assert.Equal(-100, below.DeviationPercent);
        var above = SolarPowerCalculator.Compare(estimate, new(estimate.Timestamp, estimate.UpperKw + 1, SolarPowerBasis.PvDc), options, Now);
        Assert.Equal(SolarComparisonStatus.AboveEstimate, above.Status);
        Assert.Equal(estimate.UpperKw + 1 - estimate.CentralKw, above.DeviationKw);
        Assert.Null(SolarPowerCalculator.Compare(estimate, null, options, Now).DeviationPercent);
    }

    [Fact]
    public void AcLimitClipsCombinedRoofsOnlyWhenAcExplicitlyRequested()
    {
        var options = new SolarEstimateOptions { InverterAcLimitKw = 3 };
        var dc = SolarPowerCalculator.Calculate(Observation(1000, 1000), options, Now);
        var ac = SolarPowerCalculator.Calculate(Observation(1000, 1000), options, Now, SolarPowerBasis.Ac);
        Assert.True(dc.CentralKw > 3);
        Assert.Equal(3, ac.CentralKw);
        Assert.Equal(0.97, SolarPowerCalculator.ToAc(1, options));
        Assert.Throws<ArgumentException>(() => SolarPowerCalculator.ToAc(1, new()));
    }

    [Fact]
    public void RejectsInvalidFutureAndMissingRadiationInsteadOfReturningZero()
    {
        Assert.Throws<ArgumentException>(() => SolarPowerCalculator.Calculate(Observation() with { Timestamp = Now.AddMinutes(1) }, new(), Now));
        Assert.Throws<ArgumentException>(() => SolarPowerCalculator.Calculate(Observation(double.NaN), new(), Now));
        Assert.Throws<ArgumentException>(() => SolarPowerCalculator.Calculate(Observation(-1), new(), Now));
        Assert.Throws<ArgumentException>(() => SolarPowerCalculator.Calculate(Observation(), new() { Roof1Kwp = 0, Roof2Kwp = 0 }, Now));
    }

    [Theory]
    [InlineData("2026-01-01T12:00:00Z", 13)]
    [InlineData("2026-07-01T12:00:00Z", 14)]
    public void WarsawDisplayHonorsWinterAndSummerTime(string timestamp, int hour) =>
        Assert.Equal(hour, TimeHelper.ToUserTime(DateTimeOffset.Parse(timestamp).UtcDateTime, "Europe/Warsaw").Hour);

    private static SolarEstimateOptions OnePanelWithoutOtherLossesOrUncertainty() => new()
    {
        Roof1Kwp = 0.54, Roof2Kwp = 0.54,
        FaimanU0 = 50, FaimanU1 = 0, CellTemperatureRiseAt1000 = 0,
        DcLossFraction = 0, MinimumDcLossFraction = 0, MaximumDcLossFraction = 0,
        ModelUncertaintyFraction = 0, ConfigurationUncertaintyFraction = 0, CellTemperatureUncertaintyC = 0
    };
}
