using System.Net;
using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Api;
using DeyeSolar.Web.Components.Ui;
using DeyeSolar.Web.Redesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DeyeSolar.Web.Tests;

public class RedesignDevicePresentationTests
{
    private static readonly DateTimeOffset End = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static DeviceDto Device(bool state = true) => new("device", "Garden lights", "Socket", true, state, 850, StateKnown: true);
    private static DeviceHistoryDto History(params DeviceIntervalDto[] intervals) => new(End.AddHours(-24), End, intervals, 120, 120, true);

    [Fact]
    public void StateStartUsesContiguousMatchingObservationsAndStopsAtAnUnknownGap()
    {
        var history = History(new DeviceIntervalDto(End.AddMinutes(-30), End.AddMinutes(-20), true, "provider_observation"),
            new(End.AddMinutes(-20), End.AddMinutes(-10), null, "observation_gap"),
            new(End.AddMinutes(-10), End.AddMinutes(-5), true, "provider_observation"),
            new(End.AddMinutes(-5), End, true, "provider_observation"));
        Assert.Equal(End.AddMinutes(-10), DevicePresentation.CurrentObservedStateSince(Device(), history));
    }

    [Fact]
    public void StateStartDoesNotTreatAnAcknowledgementAsAnObservedPhysicalState()
    {
        var acknowledgement = History(new DeviceIntervalDto(End.AddMinutes(-5), End, true, "provider_acknowledgement"));
        Assert.Null(DevicePresentation.CurrentObservedStateSince(Device(), acknowledgement));
        var old = History(new DeviceIntervalDto(End.AddMinutes(-15), End.AddMinutes(-5), true, "provider_observation"));
        Assert.Null(DevicePresentation.CurrentObservedStateSince(Device(), old));
        var observed = History(new DeviceIntervalDto(End.AddMinutes(-5), End, true, "provider_observation"));
        Assert.Null(DevicePresentation.CurrentObservedStateSince(Device(false), observed));
        Assert.Null(DevicePresentation.CurrentObservedStateSince(Device() with { Online = false }, observed));
        Assert.Null(DevicePresentation.CurrentObservedStateSince(Device() with { StateKnown = false }, observed));
    }

    [Fact]
    public void ManualChipRequiresAnActuallyDisabledManualOverrideRule()
    {
        var rule = new TriggerRuleDto(1, "Lights", "device", false, 75, true, 55, false, 0, 10, 60,
            null, null, false, null, null, null, "v") { PauseReason = "manual_override" };
        var detail = new DeviceDetailsDto("device", "Garden lights", Device(), null, null, null, null, 1,
            [rule], null, null, false, true);
        Assert.True(DevicePresentation.ManuallyPaused(detail));
        Assert.False(DevicePresentation.ManuallyPaused(detail with { ControllingRules = [rule with { Enabled = true }] }));
        Assert.False(DevicePresentation.ManuallyPaused(detail with { ControllingRules = [rule with { PauseReason = null }] }));
    }

    [Fact]
    public void ThresholdsPreserveSharedLegacyValuesAndExcludeDisabledOrInvalidRules()
    {
        var markers = EnergyFlowPresentation.Thresholds([
            new() { Id = 1, Name = "Split", Enabled = true, SocTurnOnThreshold = 75, UseSeparateSocTurnOffThreshold = true, SocTurnOffThreshold = 55 },
            new() { Id = 2, Name = "Shared", Enabled = true, SocTurnOnThreshold = 80, UseSeparateSocTurnOffThreshold = false, SocTurnOffThreshold = 300 },
            new() { Id = 3, Name = "Disabled", Enabled = false },
            new() { Id = 4, Name = "Invalid", Enabled = true, SocTurnOnThreshold = 101 }]);
        Assert.Equal([new EnergyFlowThreshold(1, "Split", 75, 55), new EnergyFlowThreshold(2, "Shared", 80, 80)], markers);
    }

    [Fact]
    public async Task EnergyFlowKeepsRealEnabledRuleLinksWithoutTheRemovedGauge()
    {
        var collection = new ServiceCollection(); collection.AddLogging(); collection.AddComponentLocalization();
        await using var services = collection.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => WebUtility.HtmlDecode((await renderer.RenderComponentAsync<EnergyFlow>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Data"] = new LiveReading(4, 1, -2, -1, 67, End, End, End),
                ["Rules"] = new TriggerRule[] { new() { Id = 7, Name = "Water heater", Enabled = true,
                    SocTurnOnThreshold = 75, UseSeparateSocTurnOffThreshold = true, SocTurnOffThreshold = 55 } }
            }))).ToHtmlString()));
        Assert.DoesNotContain("flow-rule-track", html); Assert.DoesNotContain("role=\"meter\"", html);
        Assert.Contains("/automations/7", html); Assert.Contains("On at 75% · off at 55%", html);
        Assert.Contains("/activity/readings", html);
    }
}
