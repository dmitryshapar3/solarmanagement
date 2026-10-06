using DeyeSolar.Domain.Billing;
using DeyeSolar.Web.Integrations;
using SolarManagement.Integrations.Contracts;

namespace DeyeSolar.Web.Tests;

public class IntegrationDiscoveryPresentationTests
{
    [Theory]
    [InlineData("{\"capabilities\":{\"canSwitch\":false}}", false)]
    [InlineData("{\"capabilities\":{\"canSwitch\":true}}", true)]
    [InlineData("{\"canSwitch\":false}", null)]
    [InlineData("{\"capabilities\":{\"canSwitch\":\"false\"}}", null)]
    [InlineData("{}", null)]
    public void SwitchingCapabilityUsesOnlyCanonicalBooleanMetadata(string json, bool? expected)
        => Assert.Equal(expected, IntegrationDiscoveryPresentation.CanSwitch(Device() with { Metadata = System.Text.Json.JsonDocument.Parse(json).RootElement.Clone() }));

    [Fact]
    public void TrialQuotaAppliesOnlyToNewSocketBindingsAndNeverBlocksASelectedReplayOrInverter()
    {
        var now = DateTimeOffset.UtcNow;
        var access = new BillingAccess("trial", true, now.AddDays(10), null, Guid.NewGuid(), 1, false, now, now.AddMinutes(5)) { SocketUsage = 1 };
        var device = Device();
        IntegrationBindingDto[] selected = [new(Guid.NewGuid(), Guid.NewGuid(), "socket", "Provider name", device.RemoteId, device.Channel, false)];
        Assert.True(IntegrationDiscoveryPresentation.RequiresPremium(access, device, []));
        Assert.False(IntegrationDiscoveryPresentation.RequiresPremium(access, device, selected));
        Assert.True(IntegrationDiscoveryPresentation.RequiresPremium(access, device with { Channel = "other" }, selected));
        Assert.False(IntegrationDiscoveryPresentation.RequiresPremium(access, device with { Kind = "inverter" }, []));
        Assert.False(IntegrationDiscoveryPresentation.RequiresPremium(access with { SocketLimit = null }, device, []));
        Assert.False(IntegrationDiscoveryPresentation.RequiresPremium(null, device, []));
    }
    private static IntegrationDiscoveryDevice Device() => new("signed-selection", "Provider name", "socket", "remote", "0", null);
}
