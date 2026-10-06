using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Components.Ui;
using SolarManagement.Inverters.Contracts;

namespace DeyeSolar.Web.Tests;

public class LiveReadingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static InverterData Reading() => ConfirmedInverterReading.Create(new()
    {
        SolarProduction = 0, SolarObservedAt = Now.AddMinutes(-1), SolarDeviceSn = "primary",
        GridConsumption = -2300, GridObservedAt = Now.AddMinutes(-2), GridDeviceSn = "primary",
        LoadPower = 700, BatteryPower = -400, BatterySoc = 65, Timestamp = Now
    });

    [Fact]
    public void MeasuredZeroAndSignedFlowsKeepTheirMeaning()
    {
        var view = LiveReading.Read(Reading(), Now, "primary");
        Assert.Equal(0, view.SolarKw); Assert.Equal(-2.3, view.GridKw);
        Assert.Equal(0.7, view.LoadKw); Assert.Equal(-0.4, view.BatteryKw); Assert.Equal(65, view.BatterySoc);
        Assert.Null(LiveReading.Read(null, Now, "primary").SolarKw);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("other source")]
    [InlineData("invalid quality")]
    public void AnUnavailableSolarMeasurementDoesNotHideIndependentGridExport(string reason)
    {
        var data = Reading();
        data = reason switch
        {
            "stale" => data with { SolarObservedAt = Now.AddMinutes(-11) },
            "future" => data with { SolarObservedAt = Now.AddSeconds(1) },
            "other source" => data with { SolarDeviceSn = "secondary" },
            _ => data with { Telemetry = ((InverterTelemetry)data.Telemetry!) with
                { SolarPower = new(new Watts(0), data.SolarObservedAt, MeasurementQuality.Invalid) } }
        };
        var view = LiveReading.Read(data, Now, "primary");
        Assert.Null(view.SolarKw); Assert.Equal(-2.3, view.GridKw);
    }

    [Fact]
    public void FreshRetrievalDoesNotRefreshOldMeasurementsAndAnOldPollCannotSupplyBatteryOrLoad()
    {
        var data = Reading() with { SolarObservedAt = Now.AddMinutes(-11), GridObservedAt = Now.AddMinutes(-11) };
        var view = LiveReading.Read(data, Now, "primary");
        Assert.Null(view.SolarKw); Assert.Null(view.GridKw); Assert.Equal(65, view.BatterySoc);
        view = LiveReading.Read(Reading() with { Timestamp = Now.AddMinutes(-11) }, Now, "primary");
        Assert.Equal(0, view.SolarKw); Assert.Equal(-2.3, view.GridKw);
        Assert.Null(view.LoadKw); Assert.Null(view.BatteryKw); Assert.Null(view.BatterySoc);
    }

    [Fact]
    public void MissingPrimaryAndInvalidLoadCannotBecomeUsableNumbers()
    {
        var view = LiveReading.Read(Reading() with { LoadPower = -20, BatterySocValid = false }, Now, null);
        Assert.Null(view.SolarKw); Assert.Null(view.GridKw); Assert.Null(view.LoadKw); Assert.Null(view.BatterySoc);
        Assert.Equal(-0.4, view.BatteryKw);
    }
}
