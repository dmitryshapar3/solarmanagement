using System.Net;
using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Shared;
using DeyeSolar.Web.Components.Ui;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SolarManagement.Inverters.Contracts;

namespace DeyeSolar.Web.Tests;

public class InverterMeasurementValidityTests
{
    [Theory]
    [InlineData(MeasurementQuality.Missing)]
    [InlineData(MeasurementQuality.Invalid)]
    public async Task MissingBatteryAndLoadDefaultsAreHiddenWithoutHidingTheValidSolarReading(MeasurementQuality quality)
    {
        var reading = Reading(quality);
        var html = await RenderAsync(reading);
        var live = LiveReading.Read(reading, Now, "source");
        Assert.Null(live.BatteryKw); Assert.Null(live.LoadKw); Assert.Null(live.BatterySoc);
        Assert.False(reading.BatteryVoltageValid); Assert.False(reading.BatteryCurrentValid); Assert.False(reading.BatteryTemperatureValid);
        Assert.Matches("<dt>Home load</dt><dd>— kW</dd>", html);
        Assert.Matches("<dt>Solar generation</dt><dd>4[.]10 kW</dd>", html);
        Assert.Null(PowerBalance.FromReading(reading, Now).Watts);
        Assert.DoesNotContain("Battery idle", html);
        Assert.DoesNotContain("reported zero may mean", html);
        Assert.Equal(4100, reading.SolarProduction);
        Assert.Equal(0, reading.BatteryPower);
    }

    [Fact]
    public async Task ValidZeroMeasurementsRemainVisibleAndUnverifiedReadingsAreUnavailable()
    {
        var valid = await RenderAsync(Reading(MeasurementQuality.Good));
        Assert.Contains("Battery idle", valid);
        Assert.Matches("<dt>Battery idle</dt><dd>0[.]00 kW</dd>", valid);
        Assert.Contains(">0%</text>", valid);
        var reading = Reading(MeasurementQuality.Good);
        Assert.True(reading.BatteryVoltageValid); Assert.True(reading.BatteryCurrentValid); Assert.True(reading.BatteryTemperatureValid);
        Assert.Equal(4100, PowerBalance.FromReading(reading, Now).Watts);
        Assert.DoesNotContain("legacy reading", valid);
        var unverified = await RenderAsync(reading with { Telemetry = null, BatterySocValid = false });
        Assert.Contains("Battery state unknown", unverified);
        Assert.Contains(">—%</text>", unverified);
        Assert.DoesNotContain("legacy reading", unverified);
    }

    [Theory]
    [InlineData(MeasurementQuality.Missing)]
    [InlineData(MeasurementQuality.Invalid)]
    [InlineData(MeasurementQuality.Stale)]
    public async Task UnavailableGridAndSolarMeasurementsNeverPresentDefaultZeroAsIdleOrGeneration(MeasurementQuality quality)
    {
        var html = await RenderAsync(Reading(MeasurementQuality.Good, quality, quality, 0));
        Assert.DoesNotContain("Grid idle", html);
        Assert.Matches("<dt>Grid state unknown</dt><dd>— kW</dd>", html);
        Assert.Matches("<dt>Solar generation</dt><dd>— kW</dd>", html);
        Assert.Null(PowerBalance.FromReading(Reading(MeasurementQuality.Good, quality, quality, 0), Now).Watts);
        Assert.Contains("Battery idle", html);
    }

    [Fact]
    public async Task GoodGridAndSolarZeroRemainVisibleOnlyWithConfirmedMeasurementQuality()
    {
        foreach (var reading in new[] { Reading(MeasurementQuality.Good, solarPower: 0) })
        {
            var html = await RenderAsync(reading);
            Assert.Matches("<dt>Grid idle</dt><dd>0[.]00 kW</dd>", html);
            Assert.Matches("<dt>Solar generation</dt><dd>0[.]00 kW</dd>", html);
            Assert.Equal(0, PowerBalance.FromReading(reading, Now).Watts);

        }
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 4, 10, 0, 0, TimeSpan.Zero);
    private static InverterData Reading(MeasurementQuality quality, MeasurementQuality gridQuality = MeasurementQuality.Good,
        MeasurementQuality solarQuality = MeasurementQuality.Good, int solarPower = 4100)
    {
        var telemetry = new InverterTelemetry(new(Guid.NewGuid()), Now,
            new(new Percent(0), Now, quality), new(new Watts(0), Now, quality),
            new(new Celsius(0), Now, quality), new(new Volts(0), Now, quality), new(new Amperes(0), Now, quality),
            new(new Watts(solarPower), Now, solarQuality), new(new Watts(0), Now, gridQuality),
            new(new Watts(0), Now, quality), SolarManagement.Inverters.Contracts.SolarPowerBasis.PvDc);
        return new()
        {
            Telemetry = telemetry,
            BatterySocValid = quality == MeasurementQuality.Good,
            SolarProduction = solarPower,
            SolarObservedAt = Now,
            GridObservedAt = Now,
            SolarDeviceSn = "source",
            GridDeviceSn = "source",
            Timestamp = Now
        };
    }
    private static async Task<string> RenderAsync(InverterData reading)
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddComponentLocalization();
        await using var services = collection.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => WebUtility.HtmlDecode((await renderer.RenderComponentAsync<EnergyFlow>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["Data"] = LiveReading.Read(reading, Now, "source") }))).ToHtmlString()));
    }
}
