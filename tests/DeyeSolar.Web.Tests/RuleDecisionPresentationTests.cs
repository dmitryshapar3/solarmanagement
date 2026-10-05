using DeyeSolar.Domain.Models;
using DeyeSolar.RuleEngine;
using DeyeSolar.Web.Workers;

namespace DeyeSolar.Web.Tests;

public sealed class RuleDecisionPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static InverterData Reading(int soc) => ConfirmedInverterReading.Create(new() { BatterySoc = soc, Timestamp = Now });

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("stale")]
    [InlineData("future")]
    public void UnavailableSocIsNotCapturedAsANumericMeasurement(string scenario)
    {
        var reading = scenario switch
        {
            "missing" => Reading(90) with { Telemetry = null },
            "invalid" => Reading(90) with { BatterySocValid = false },
            "stale" => ConfirmedInverterReading.Create(new() { BatterySoc = 90, Timestamp = Now.AddMinutes(-11) }),
            _ => ConfirmedInverterReading.Create(new() { BatterySoc = 90, Timestamp = Now.AddMinutes(1) })
        };
        var rule = new TriggerRule { SocTurnOnThreshold = 80 };
        var decision = new RuleEvaluator().Decide(reading, rule, Now);
        Assert.Equal(RuleDecisionReason.MeasurementUnavailable, decision.Reason);
        Assert.Null(decision.BatterySoc);
        Assert.Null(decision.Action);

        rule.CurrentState = true;
        rule.ActiveFrom = new(8, 0);
        rule.ActiveTo = new(9, 0);
        var timeWindowOff = new RuleEvaluator().Decide(reading, rule, Now);
        Assert.False(timeWindowOff.TurnOn);
        Assert.Null(timeWindowOff.BatterySoc);
    }

    [Fact]
    public void MeasuredZeroSocIsPreservedAndCanAuthorizeThresholdOff()
    {
        var decision = new RuleEvaluator().Decide(Reading(0), new TriggerRule
            { CurrentState = true, SocTurnOnThreshold = 20 }, Now);
        Assert.Equal(0, decision.BatterySoc);
        Assert.Equal(RuleDecisionReason.SocReachedTurnOffThreshold, decision.Reason);
        Assert.False(decision.TurnOn);
    }

    [Fact]
    public void TimeWindowOffIsExplainedByItsActualDecisionEvenWhenSocAlsoCrossesTheThreshold()
    {
        var rule = new TriggerRule { Id = 1, EntityId = Guid.NewGuid().ToString("D"), CurrentState = true,
            SocTurnOnThreshold = 80, ActiveFrom = new(8, 0), ActiveTo = new(9, 0) };
        var decision = new RuleEvaluator().Decide(Reading(20), rule, Now);
        Assert.Equal(RuleDecisionReason.OutsideActiveWindow, decision.Reason);
        Assert.False(decision.TurnOn);
        var presentation = RuleRunPresentation.From(new(rule.Name, decision, true, null));
        Assert.Equal("OFF", presentation.Action);
        Assert.Equal("action:off:time-window", presentation.ConditionKey);
    }

    [Fact]
    public void RecordedCooldownKeepsTheEvaluatedValuesAfterTheRuleChanges()
    {
        var rule = new TriggerRule { Id = 1, EntityId = Guid.NewGuid().ToString("D"), SocTurnOnThreshold = 80,
            CooldownMinutes = 15, CurrentStateChangedAt = Now.AddMinutes(-2).UtcDateTime };
        var decision = new RuleEvaluator().Decide(Reading(90), rule, Now);
        rule.CooldownMinutes = 0;
        rule.SocTurnOnThreshold = 100;
        rule.CurrentStateChangedAt = Now.AddHours(-1).UtcDateTime;
        var presentation = RuleRunPresentation.From(new(rule.Name, decision, false, null));
        Assert.Equal("no-change:off:cooldown", presentation.ConditionKey);
        Assert.Contains("13 min remaining", presentation.Reason);
        Assert.Null(decision.Action);
    }

    [Fact]
    public void RecordedSolarBlockKeepsItsCapturedAverageAndRequirement()
    {
        var rule = new TriggerRule { Id = 1, EntityId = Guid.NewGuid().ToString("D"), SocTurnOnThreshold = 80,
            UseSolarProductionThreshold = true, MinAverageSolarProductionWatts = 4000 };
        var decision = new RuleEvaluator().Decide(Reading(90), rule, Now, context: new(3000));
        rule.MinAverageSolarProductionWatts = 1000;
        rule.UseSolarProductionThreshold = false;
        var presentation = RuleRunPresentation.From(new(rule.Name, decision, false, null));
        Assert.Equal("no-change:off:solar-average-below-threshold", presentation.ConditionKey);
        Assert.Contains("3000W", presentation.Reason);
        Assert.Contains("4000W", presentation.Reason);
        Assert.Null(decision.Action);
    }
}
