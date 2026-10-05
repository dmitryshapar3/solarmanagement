using DeyeSolar.Web.Integrations;
using SolarManagement.Inverters.Contracts;

namespace DeyeSolar.Web.Tests;

public sealed class IntegrationCapabilityPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("invalid json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("{\"capabilities\":null}")]
    [InlineData("{\"capabilities\":[]}")]
    [InlineData("{\"capabilities\":{\"canSwitch\":\"true\",\"hasBattery\":1,\"solarBasis\":\"999\"}}")]
    public void MalformedOrUntypedMetadataCannotGrantDeviceCapabilities(string? json)
    {
        var capabilities = IntegrationCapabilities.ReadMetadata(json);
        Assert.False(capabilities.Socket.CanSwitch);
        Assert.False(capabilities.Socket.CanMeasurePower);
        Assert.False(capabilities.Inverter.HasBattery);
        Assert.False(capabilities.Inverter.HasSolarPower);
        Assert.Equal(SolarPowerBasis.Unknown, capabilities.Inverter.SolarBasis);
    }

    [Fact]
    public void SocketAndInverterPoliciesReadTheSameStrictMetadata()
    {
        var capabilities = IntegrationCapabilities.ReadMetadata("{\"capabilities\":{\"canSwitch\":true,\"canMeasurePower\":false,\"hasBattery\":true,\"hasSolarPower\":true,\"hasSignedGridPower\":true,\"hasLoadPower\":true,\"hasGridPowerHistory\":true,\"solarBasis\":\"PvDc\"}}");
        Assert.True(capabilities.Socket.CanSwitch);
        Assert.False(capabilities.Socket.CanMeasurePower);
        Assert.True(capabilities.Inverter.HasBattery);
        Assert.True(capabilities.Inverter.HasSolarPower);
        Assert.True(capabilities.Inverter.HasSignedGridPower);
        Assert.True(capabilities.Inverter.HasLoadPower);
        Assert.True(capabilities.Inverter.HasGridPowerHistory);
        Assert.Equal(SolarPowerBasis.PvDc, capabilities.Inverter.SolarBasis);
    }
}
