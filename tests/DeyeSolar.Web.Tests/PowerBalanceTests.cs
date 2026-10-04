using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Shared;
using DeyeSolar.Web.Api;

namespace DeyeSolar.Web.Tests;

public class PowerBalanceTests
{
    private static readonly DateTimeOffset Observed = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
    private static InverterData Reading() => new()
    {
        SolarProduction = 4100, GridConsumption = -1200, BatteryPower = -2000, LoadPower = 900,
        SolarObservedAt = Observed, GridObservedAt = Observed, Timestamp = Observed.AddMinutes(1),
        SolarDeviceSn = "inverter-a", GridDeviceSn = "inverter-a"
    };

    [Theory]
    [InlineData(4100, -1200, -2000, 900, 0)]
    [InlineData(0, 1000, 500, 1500, 0)]
    [InlineData(0, 3000, -2000, 1000, 0)]
    [InlineData(5000, -6000, 1500, 500, 0)]
    [InlineData(4100, -1200, -1800, 900, 200)]
    [InlineData(4100, -1200, -2200, 900, -200)]
    [InlineData(0, 0, 0, 0, 0)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue, 0, 6442450941d)]
    public void BalanceUsesSignedSupplyAndDemandWithoutIntegerOverflow(double solar, double grid, double battery, double load, double expected)
        => Assert.Equal(expected, PowerBalance.Calculate(solar, grid, battery, load));

    [Fact]
    public void MissingNonfiniteAndNegativeRequiredReadingsAreUnavailable()
    {
        Assert.Null(PowerBalance.Calculate(null, 0, 0, 0));
        Assert.Null(PowerBalance.Calculate(0, null, 0, 0));
        Assert.Null(PowerBalance.Calculate(0, 0, null, 0));
        Assert.Null(PowerBalance.Calculate(0, 0, 0, null));
        Assert.Null(PowerBalance.Calculate(double.NaN, 0, 0, 0));
        Assert.Null(PowerBalance.Calculate(0, double.PositiveInfinity, 0, 0));
        Assert.Null(PowerBalance.Calculate(0, 0, double.NegativeInfinity, 0));
        Assert.Null(PowerBalance.Calculate(0, 0, 0, double.NaN));
        Assert.Null(PowerBalance.Calculate(-1, 0, 0, 0));
        Assert.Null(PowerBalance.Calculate(0, 0, 0, -1));
    }

    [Fact]
    public void OnlyRecentAlignedMeasurementsFromTheSameConfiguredInverterSupportABalance()
    {
        var now = Observed.AddMinutes(2);
        Assert.Equal(0d, PowerBalance.FromReading(Reading(), now, expectedDeviceSn: "inverter-a").Watts);
        Assert.Null(PowerBalance.FromReading(null, now).Watts);
        Assert.Null(PowerBalance.FromReading(Reading() with { SolarObservedAt = null }, now).Watts);
        Assert.Null(PowerBalance.FromReading(Reading() with { GridObservedAt = null }, now).Watts);
        Assert.Null(PowerBalance.FromReading(Reading() with { GridDeviceSn = "inverter-b" }, now).Watts);
        Assert.Null(PowerBalance.FromReading(Reading(), now, expectedDeviceSn: "inverter-b").Watts);
        Assert.Null(PowerBalance.FromReading(Reading() with { SolarObservedAt = Observed.AddMinutes(-3) }, now).Watts);
        Assert.Null(PowerBalance.FromReading(Reading() with { SolarObservedAt = Observed.AddMinutes(3) }, now).Watts);
        Assert.Null(PowerBalance.FromReading(Reading(), Observed.AddMinutes(11)).Watts);
        Assert.Null(PowerBalance.FromReading(Reading() with { Timestamp = Observed.AddMinutes(-1) }, now).Watts);
    }

    [Theory]
    [InlineData(-200, "Reported consumption exceeds supply")]
    [InlineData(200, "Reported supply exceeds consumption")]
    [InlineData(0, "Reported supply and consumption match")]
    public void DirectionRetainsTheDifferenceSign(double value, string expected)
        => Assert.Equal(expected, PowerBalance.Direction(value));

    [Fact]
    public void MobileMappingPreservesSourceMetadataWithoutReplacingObservationTimesWithPollTime()
    {
        var reading = Reading();
        var dto = reading.ToDto();
        Assert.Equal(reading.SolarObservedAt, dto.SolarObservedAt);
        Assert.Equal(reading.GridObservedAt, dto.GridObservedAt);
        Assert.Equal("inverter-a", dto.SolarDeviceSn);
        Assert.Equal("inverter-a", dto.GridDeviceSn);
        Assert.NotEqual(dto.Timestamp, dto.SolarObservedAt);
        var missing = (reading with { SolarObservedAt = null, GridObservedAt = null, SolarDeviceSn = null, GridDeviceSn = null }).ToDto();
        Assert.Null(missing.SolarObservedAt);
        Assert.Null(missing.GridObservedAt);
    }
}
