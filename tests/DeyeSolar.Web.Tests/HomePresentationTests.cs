using DeyeSolar.Web.Components.Ui;
using DeyeSolar.Web.Redesign;

namespace DeyeSolar.Web.Tests;

public class HomePresentationTests
{
    private static readonly DateTimeOffset Midnight = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
    internal static ProductionViewDto Day() => new(Midnight, Midnight.AddDays(1), "UTC",
        new(2026, 10, 6), new(2026, 10, 6), [], [], null, null, 0, 86400,
        null, null, null, Midnight.AddHours(6), Midnight.AddHours(18), Midnight.AddHours(30),
        null, null, null, true);

    [Theory]
    [InlineData(2, 6)]
    [InlineData(6, -1)]
    [InlineData(12, -1)]
    [InlineData(18, 30)]
    [InlineData(23, 30)]
    public void NightUsesAstronomyIncludingBeforeDawn(int hour, int next)
    {
        Assert.Equal(next < 0 ? (DateTimeOffset?)null : Midnight.AddHours(next),
            HomePresentation.NightSunrise(Day(), Midnight.AddHours(hour)));
    }

    [Fact]
    public void AbsentAstronomyAndOldSelectedDaysDoNotInventNight()
    {
        Assert.Null(HomePresentation.NightSunrise(null, Midnight));
        Assert.Null(HomePresentation.NightSunrise(Day() with { Sunrise = null, Sunset = null }, Midnight));
        Assert.Null(HomePresentation.NightSunrise(Day() with { Date = new(2026, 10, 5) }, Midnight));
        Assert.Null(HomePresentation.NightSunrise(Day() with { NextSunrise = null }, Midnight.AddHours(20)));
        Assert.Null(HomePresentation.NightSunrise(Day() with { TimeZoneId = "Unknown/Zone" }, Midnight));
    }

    [Theory]
    [InlineData(0d, 1d, 0d, 1d, true)]
    [InlineData(0d, 1d, .2d, 1d, false)]
    [InlineData(.5d, 1d, 0d, 1d, false)]
    [InlineData(0d, -1d, 0d, 1d, false)]
    [InlineData(null, 1d, 0d, 1d, false)]
    [InlineData(0d, null, 0d, 1d, false)]
    [InlineData(0d, 1d, null, 1d, false)]
    [InlineData(0d, 1d, 0d, null, false)]
    public void BatterySentenceRequiresVerifiedDischargeWithoutImport(double? solar, double? battery,
        double? grid, double? load, bool expected)
    {
        Assert.Equal(expected, HomePresentation.RunsOnBattery(new(solar, load, grid, battery, 50, null, null, null)));
    }
}
