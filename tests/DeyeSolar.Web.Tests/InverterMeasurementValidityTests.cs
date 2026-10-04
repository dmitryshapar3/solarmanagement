using System.Net;
using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Shared;
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
        foreach (var (field, unit) in new[] { ("battery-power", "W"), ("load", "W"), ("soc", "%"), ("voltage", "V"), ("current", "A"), ("temperature", "°C") })
            Assert.Matches($"data-testid=\"inverter-{field}\"[^>]*>— {unit}", html);
        Assert.Matches("data-testid=\"inverter-pv\"[^>]*>4[.]10 kW", html);
        Assert.Matches("data-testid=\"inverter-balance\"[^>]*>—", html);
        Assert.DoesNotContain("Battery idle", html);
        Assert.DoesNotContain("reported zero may mean", html);
        Assert.Equal(4100, reading.SolarProduction);
        Assert.Equal(0, reading.BatteryPower);
    }

    [Fact]
    public async Task ValidZeroMeasurementsRemainVisibleAndHistoricalReadingsKeepTheirLegacyMeaning()
    {
        var valid = await RenderAsync(Reading(MeasurementQuality.Good));
        Assert.Contains("Battery idle", valid);
        Assert.Matches("data-testid=\"inverter-battery-power\"[^>]*>0 W", valid);
        Assert.Matches("data-testid=\"inverter-soc\"[^>]*>0 %", valid);
        Assert.Matches("data-testid=\"inverter-voltage\"[^>]*>0[.]00 V", valid);
        Assert.Matches("data-testid=\"inverter-balance\"[^>]*>\\+4,100 W", valid);
        Assert.DoesNotContain("legacy reading", valid);
        var legacy = await RenderAsync(Reading(MeasurementQuality.Good) with { Telemetry = null, BatterySocValid = null });
        Assert.Matches("data-testid=\"inverter-battery-power\"[^>]*>0 W", legacy);
        Assert.Matches("data-testid=\"inverter-soc\"[^>]*>0 %", legacy);
        Assert.Contains("legacy reading", legacy);
    }

    [Theory]
    [InlineData(MeasurementQuality.Missing)]
    [InlineData(MeasurementQuality.Invalid)]
    [InlineData(MeasurementQuality.Stale)]
    public async Task UnavailableGridAndSolarMeasurementsNeverPresentDefaultZeroAsIdleOrGeneration(MeasurementQuality quality)
    {
        var html = await RenderAsync(Reading(MeasurementQuality.Good, quality, quality, 0));
        Assert.DoesNotContain(">Idle<", html);
        Assert.Matches("data-testid=\"inverter-grid\"[^>]*>— W", html);
        Assert.Matches("data-testid=\"inverter-pv\"[^>]*>— kW", html);
        Assert.Contains("Grid measurement unavailable", html);
        Assert.Matches("data-testid=\"inverter-balance\"[^>]*>—", html);
        Assert.Contains("Battery idle", html);
    }

    [Fact]
    public async Task GoodGridAndSolarZeroRemainVisibleAndNullLegacyValidityPreservesHistoricalZero()
    {
        foreach (var reading in new[] { Reading(MeasurementQuality.Good, solarPower: 0),
            Reading(MeasurementQuality.Good, solarPower: 0) with { Telemetry = null } })
        {
            var html = await RenderAsync(reading);
            Assert.Matches("data-testid=\"inverter-grid\"[^>]*>0 W", html);
            Assert.Matches("data-testid=\"inverter-pv\"[^>]*>0[.]00 kW", html);
            Assert.Contains(">Idle<", html);
            Assert.Matches("data-testid=\"inverter-balance\"[^>]*>0 W", html);
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
        await using var services = collection.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => WebUtility.HtmlDecode((await renderer.RenderComponentAsync<InverterReadings>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["Data"] = reading, ["Now"] = Now }))).ToHtmlString()));
    }
}
