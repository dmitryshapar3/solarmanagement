using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;

namespace DeyeSolar.Web.Tests;

public class SolarNowcastTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 18, 11, 0, 0, TimeSpan.Zero);
    private static SolarRadiationObservation Forecast() => new(Start, 200, 100, 10, 2, Start, 0)
    {
        Kind = SolarRadiationKind.WeatherModel, RetrievedAt = Start,
        Forecast = [new(Start, 200, 100, 10, 2, 20), new(Start.AddMinutes(15), 800, 400, 20, 4, 60),
            new(Start.AddMinutes(30), 400, 200, 15, 3, 80)]
    };

    [Fact]
    public void EvaluatesBothRoofInstantValuesAtCurrentMinuteWithTraceableBracket()
    {
        var now = Start.AddMinutes(5);
        var result = SolarNowcast.Interpolate(Forecast(), now);
        Assert.Equal(now, result.Timestamp);
        Assert.Equal(400, result.Roof1Gti);
        Assert.Equal(200, result.Roof2Gti);
        Assert.Equal(10 + 10d / 3, result.AirTemperatureC!.Value, 8);
        Assert.Equal(20 + 40d / 3, result.CloudCoverPercent!.Value, 8);
        Assert.Equal(Start, result.ModelPeriodStart);
        Assert.Equal(Start.AddMinutes(15), result.ModelPeriodEnd);
        Assert.Equal(Start, result.RetrievedAt);
        Assert.Equal(now, result.WeatherTimestamp);
    }

    [Fact]
    public void DoesNotExtrapolateBridgeMissingSamplesOrRelabelSatelliteAsNow()
    {
        var source = Forecast();
        Assert.Throws<InvalidDataException>(() => SolarNowcast.Interpolate(source, Start.AddSeconds(-1)));
        Assert.Throws<InvalidDataException>(() => SolarNowcast.Interpolate(source, Start.AddMinutes(31)));
        Assert.Throws<InvalidDataException>(() => SolarNowcast.Interpolate(source with
            { Forecast = [source.Forecast![0], source.Forecast[2]] }, Start.AddMinutes(10)));
        Assert.Throws<InvalidDataException>(() => SolarNowcast.Interpolate(source with
            { Kind = SolarRadiationKind.Satellite }, Start.AddMinutes(5)));
    }

    [Fact]
    public void HandlesExactTimestampNightAndMissingWeatherWithoutInventingConditions()
    {
        var source = Forecast() with { Forecast = [new(Start, 0, 0, null, null, null)] };
        var result = SolarNowcast.Interpolate(source, Start);
        var estimate = SolarPowerCalculator.Calculate(result, new(), Start);
        Assert.Equal(0, estimate.CentralKw);
        Assert.True(estimate.WeatherMissing);
        Assert.Null(result.CloudCoverPercent);
        Assert.Null(SolarPowerCalculator.Compare(estimate, new(Start, 0, SolarPowerBasis.PvDc), new(), Start).DeviationPercent);
    }

    [Fact]
    public void RecentCalculationDoesNotResetWeatherRetrievalAge()
    {
        var now = Start.AddMinutes(25);
        var estimate = SolarPowerCalculator.Calculate(SolarNowcast.Interpolate(Forecast(), now), new(), now);
        var comparison = SolarPowerCalculator.Compare(estimate, new(now, estimate.CentralKw, SolarPowerBasis.PvDc), new(), now);
        Assert.Equal(SolarComparisonStatus.InsufficientData, comparison.Status);
        Assert.Null(comparison.DeviationPercent);
    }

    [Fact]
    public void RejectsNonfiniteIrradianceAndDuplicateTimes()
    {
        var source = Forecast();
        Assert.Throws<InvalidDataException>(() => SolarNowcast.Interpolate(source with
            { Forecast = [source.Forecast![0], source.Forecast[0]] }, Start));
        Assert.Throws<InvalidDataException>(() => SolarNowcast.Interpolate(source with
            { Forecast = [source.Forecast![0] with { Roof1Gti = double.NaN }] }, Start));
    }
}
