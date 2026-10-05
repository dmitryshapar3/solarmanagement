using DeyeSolar.Domain.Options;
using DeyeSolar.Web.Services;

namespace DeyeSolar.Web.Tests;

public sealed class SolarSiteGeometryTests
{
    [Theory]
    [InlineData(91, 0, 5, 20, 180)]
    [InlineData(0, 181, 5, 20, 180)]
    [InlineData(0, 0, 0, 20, 180)]
    [InlineData(0, 0, -1, 20, 180)]
    [InlineData(0, 0, 5, 91, 180)]
    [InlineData(0, 0, 5, 20, 360)]
    public void InvalidPhysicalGeometryIsRejectedByBothEditableSettingsAndTheModel(double latitude, double longitude, double kwp, double tilt, double azimuth)
    {
        var site = Site(latitude, longitude, kwp, tilt, azimuth);
        Assert.False(SiteSettingsService.TryValidate(site, out _));
        var options = new SolarEstimateOptions { Latitude = latitude, Longitude = longitude, Roof1Kwp = kwp, Roof2Kwp = 0,
            Roof1Tilt = tilt, Roof1Azimuth = azimuth };
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void ApiCapacityAndStringLimitsRemainDistinctFromPhysicalModelInvariants()
    {
        var site = Site(0, 0, 10001, 20, 180);
        Assert.False(SiteSettingsService.TryValidate(site, out _));
        new SolarEstimateOptions { Latitude = 0, Longitude = 0, Roof1Kwp = 10001, Roof2Kwp = 0 }.Validate();
        var valid = Site(0, 0, 5, 20, 180);
        Assert.True(SiteSettingsService.TryValidate(valid, out _));
        Assert.False(SiteSettingsService.TryValidate(valid with { SolarEstimate = valid.SolarEstimate with { LocationLabel = "invalid\nlocation" } }, out _));
        Assert.False(SiteSettingsService.TryValidate(valid with { SolarEstimate = valid.SolarEstimate with { Roof1Kwp = double.PositiveInfinity } }, out _));
    }

    private static SiteSettingsDto Site(double latitude, double longitude, double kwp, double tilt, double azimuth)
        => new(new(latitude, longitude, "Fictional site", "UTC", kwp, 0, tilt, 0, azimuth, 0), new("2026-10-01", "UTC", false));
}
