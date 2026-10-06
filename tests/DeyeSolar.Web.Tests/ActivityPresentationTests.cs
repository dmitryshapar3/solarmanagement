using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Components.Ui;
namespace DeyeSolar.Web.Tests;
public class ActivityPresentationTests
{
    [Theory]
    [InlineData(RuleDecisionReason.Disabled)]
    [InlineData(RuleDecisionReason.OutsideActiveWindow)]
    [InlineData(RuleDecisionReason.MeasurementUnavailable)]
    [InlineData(RuleDecisionReason.SocBelowTurnOnThreshold)]
    [InlineData(RuleDecisionReason.SocReachedTurnOffThreshold)]
    [InlineData(RuleDecisionReason.RemainOn)]
    [InlineData(RuleDecisionReason.Cooldown)]
    [InlineData(RuleDecisionReason.SolarAverageUnavailable)]
    [InlineData(RuleDecisionReason.SolarAverageBelowThreshold)]
    [InlineData(RuleDecisionReason.TurnOnConditionsSatisfied)]
    public void EveryRecordedPolicyDecisionHasAHumanExplanation(RuleDecisionReason reason)
        => Assert.NotEqual("See recorded evidence for this result", ActivityPresentation.Reason(reason.ToString()));

    [Fact]
    public void AManualRequestAndAnUnconfirmedCommandDoNotImplyDeviceConfirmation()
    {
        Assert.Equal("Manual switch requested", ActivityPresentation.Kind("command.manual"));
        Assert.Equal("Command result is unconfirmed", ActivityPresentation.Reason("indeterminate"));
        Assert.Equal("Provider acknowledged the command", ActivityPresentation.Reason("acknowledged"));
    }
}
