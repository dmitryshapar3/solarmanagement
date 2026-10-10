using System.Net;
using DeyeSolar.Web.Components.Ui;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DeyeSolar.Web.Tests;

public class RoofSunDiagramTests
{
    [Fact]
    public void SolarPositionAgreesWithPublishedSpaExampleWithinApproximateModelAccuracy()
    {
        // NREL SPA reference example, Colorado 2003-10-17 12:30:30 -07:00:
        // geometric elevation 39.872046°, azimuth 194.34024°. NOAA's compact approximation is less precise.
        // Reference fixture: https://github.com/pvlib/pvlib-python/blob/main/tests/test_solarposition.py
        var point = RoofSunGeometry.Position(39.742476, -105.1786, DateTimeOffset.Parse("2003-10-17T19:30:30Z"));
        Assert.InRange(point.Elevation, 39.872046 - .3, 39.872046 + .3);
        Assert.InRange(point.Azimuth, 194.34024 - .3, 194.34024 + .3);
    }

    [Theory]
    [InlineData("2026-03-29T12:00:00Z", 23)]
    [InlineData("2026-10-25T12:00:00Z", 25)]
    public void SolarPathUsesTheFullSiteCalendarDayAcrossDst(string instant, int hours)
    {
        var day = Assert.IsType<SolarDiagramDay>(RoofSunGeometry.Day(50.095278, 20.070278, "Europe/Warsaw", DateTimeOffset.Parse(instant)));
        Assert.Equal(hours, (day.EndsAt - day.StartsAt).TotalHours);
        Assert.Equal("normal", day.State);
        Assert.NotNull(day.Sunrise); Assert.NotNull(day.Sunset);
        Assert.InRange(day.Sunrise.Value, day.StartsAt, day.EndsAt);
        Assert.InRange(day.Sunset.Value, day.StartsAt, day.EndsAt);
        Assert.True(day.Sunrise < day.Sunset);
        var path = Assert.Single(day.Paths);
        Assert.InRange(Math.Abs(path[0].Elevation), 0, .00001);
        Assert.InRange(Math.Abs(path[^1].Elevation), 0, .00001);
        Assert.All(path, point => { Assert.True(double.IsFinite(point.X)); Assert.True(double.IsFinite(point.Y)); Assert.InRange(point.X, 48, 272); Assert.InRange(point.Y, 48, 272); });
    }

    [Fact]
    public void SiteDateAndLongitudeMatterAndSouthernSunFacesNorth()
    {
        var instant = DateTimeOffset.Parse("2026-01-01T00:30:00Z");
        Assert.Equal(new DateOnly(2025, 12, 31), RoofSunGeometry.Day(21.3, -157.8, "Pacific/Honolulu", instant)!.Date);
        Assert.Equal(new DateOnly(2026, 1, 1), RoofSunGeometry.Day(-36.8, 174.7, "Pacific/Auckland", instant)!.Date);
        var southern = RoofSunGeometry.Position(-33.8688, 151.2093, DateTimeOffset.Parse("2026-01-15T01:00:00Z"));
        Assert.True(southern.Elevation > 60);
        Assert.True(southern.Y < RoofSunGeometry.Center);
        Assert.NotEqual(RoofSunGeometry.Position(50, 0, instant).Azimuth, RoofSunGeometry.Position(50, 30, instant).Azimuth);
    }

    [Theory]
    [InlineData(78d, "2026-06-21T12:00:00Z", "polar-day", true)]
    [InlineData(78d, "2026-12-21T12:00:00Z", "polar-night", false)]
    [InlineData(-78d, "2026-06-21T12:00:00Z", "polar-night", false)]
    [InlineData(-78d, "2026-12-21T12:00:00Z", "polar-day", true)]
    public void PolarDaysDoNotInventSunriseOrSunset(double latitude, string instant, string state, bool visible)
    {
        var day = RoofSunGeometry.Day(latitude, 15, "UTC", DateTimeOffset.Parse(instant))!;
        Assert.Equal(state, day.State); Assert.Null(day.Sunrise); Assert.Null(day.Sunset);
        Assert.Equal(visible, day.Now is not null);
        Assert.Equal(visible, day.Paths.Count > 0);
    }

    [Theory]
    [InlineData(double.NaN, 20d, "UTC")]
    [InlineData(91d, 20d, "UTC")]
    [InlineData(50d, double.PositiveInfinity, "UTC")]
    [InlineData(50d, 181d, "UTC")]
    [InlineData(50d, 20d, "not/a-zone")]
    public void InvalidLocationNeverProducesAPlausibleSunPath(double latitude, double longitude, string zone)
        => Assert.Null(RoofSunGeometry.Day(latitude, longitude, zone, DateTimeOffset.UtcNow));

    [Fact]
    public void RoofGeometryKeepsTrueBearingsAndTiltAndOmitsUnusedOrInvalidGroups()
    {
        var roofs = RoofSunGeometry.Roofs(4, 25, 230, 3, 90, 50);
        Assert.Equal(2, roofs.Count); Assert.Equal(230, roofs[0].Azimuth); Assert.Equal(50, roofs[1].Azimuth);
        Assert.True(roofs[0].Depth > roofs[1].Depth);
        Assert.True(roofs[0].Width > roofs[1].Width);
        var roof = Assert.Single(RoofSunGeometry.Roofs(0, 25, 230, 3, 0, 360));
        Assert.Equal(2, roof.Number); Assert.Equal(0, roof.Azimuth); Assert.Equal(160, roof.CenterX);
        Assert.Empty(RoofSunGeometry.Roofs(4, double.NaN, 230, 3, 25, 361));
    }

    [Fact]
    public async Task ComponentBindsCurrentDraftToConnectedRoofAndOrbitControls()
    {
        var parameters = new Dictionary<string, object?> { ["Latitude"] = 50.095278, ["Longitude"] = 20.070278, ["TimeZoneId"] = "Europe/Warsaw",
            ["Roof1Kwp"] = 4.32d, ["Roof1Tilt"] = 25d, ["Roof1Azimuth"] = 230d,
            ["Roof1PanelCount"] = 8, ["Roof1PanelsPerRow"] = 4, ["At"] = DateTimeOffset.Parse("2026-10-09T10:00:00Z") };
        var before = await Render(parameters);
        if (Environment.GetEnvironmentVariable("SOLAR_ROOF_QA_DIRECTORY") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "component.html"), before);
        }
        Assert.Contains("Sun path · 9 Oct 2026", before); Assert.Contains("data-roof=\"1\"", before);
        Assert.DoesNotContain("data-roof=\"2\"", before);
        Assert.Contains("roof-scene-roof", before); Assert.Contains("roof-scene-wall", before);
        Assert.Equal(8, System.Text.RegularExpressions.Regex.Matches(before, "class=\"roof-scene-face roof-scene-panel\"").Count);
        Assert.Contains("8 panels", before);
        Assert.Contains("data-roof-action=\"zoom-in\"", before); Assert.Contains("data-roof-action=\"reset\"", before);
        Assert.Contains("Tilt", before);
        var beforeRoof = RoofPolygon(before);
        parameters["Roof1Azimuth"] = 90d; parameters["Roof1Tilt"] = 60d; parameters["Latitude"] = -33.8688;
        var after = await Render(parameters);
        Assert.NotEmpty(beforeRoof); Assert.NotEqual(beforeRoof, RoofPolygon(after));
        parameters["Latitude"] = double.NaN;
        var invalid = await Render(parameters); Assert.DoesNotContain("roof-scene-sun-path", invalid); Assert.Contains("roof-scene-roof", invalid); Assert.Contains("Enter valid coordinates", invalid);
        parameters["Roof1PanelCount"] = 7;
        var seven = await Render(parameters);
        Assert.Equal(7, System.Text.RegularExpressions.Regex.Matches(seven, "class=\"roof-scene-face roof-scene-panel\"").Count);
        parameters["Roof1PanelCount"] = null;
        var unknown = await Render(parameters);
        Assert.DoesNotContain("class=\"roof-scene-face roof-scene-panel\"", unknown);
        Assert.Contains("Enter the panel count for each roof", unknown);
    }

    private static string RoofPolygon(string html) => System.Text.RegularExpressions.Regex.Match(html,
        "class=\"roof-scene-face roof-scene-roof\" data-roof=\"1\" points=\"([^\"]+)\"").Groups[1].Value;

    private static async Task<string> Render(Dictionary<string, object?> parameters)
    {
        var collection = new ServiceCollection(); collection.AddLogging(); collection.AddComponentLocalization();
        await using var services = collection.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => WebUtility.HtmlDecode((await renderer.RenderComponentAsync<RoofSunDiagram>(ParameterView.FromDictionary(parameters))).ToHtmlString()));
    }
}
