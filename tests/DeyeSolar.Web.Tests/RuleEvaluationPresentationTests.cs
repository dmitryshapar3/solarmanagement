using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Redesign;

namespace DeyeSolar.Web.Tests;

public sealed class RuleEvaluationPresentationTests
{
    private static readonly TriggerRule Rule = new() { CooldownMinutes=15, ActiveFrom=new(7,0), ActiveTo=new(20,0) };
    private static RuleDecision Decision(RuleDecisionReason reason,int? soc=90,int? solar=4000,double cooldown=0,bool bypass=false)
        =>new(1,"fixture",DateTimeOffset.UtcNow,null,reason,soc,80,60,cooldown,true,bypass,solar,3000);

    [Theory]
    [InlineData(RuleDecisionReason.MeasurementUnavailable,null,"unknown")]
    [InlineData(RuleDecisionReason.TurnOnConditionsSatisfied,90,"passed")]
    public void MeasurementFreshnessUsesRecordedMeasuredEvidence(RuleDecisionReason reason,int? soc,string status)
        =>Assert.Equal(status,RuleEvaluationPresentation.Conditions(Rule,Decision(reason,soc)).Single(c=>c.Kind=="measurement_freshness").Status);

    [Theory]
    [InlineData(RuleDecisionReason.SocBelowTurnOnThreshold,70,80,"blocked")]
    [InlineData(RuleDecisionReason.TurnOnConditionsSatisfied,90,80,"passed")]
    [InlineData(RuleDecisionReason.SocReachedTurnOffThreshold,59,60,"passed")]
    [InlineData(RuleDecisionReason.RemainOn,90,60,"blocked")]
    [InlineData(RuleDecisionReason.MeasurementUnavailable,null,80,"unknown")]
    [InlineData(RuleDecisionReason.Disabled,90,80,"skipped")]
    [InlineData(RuleDecisionReason.OutsideActiveWindow,90,80,"skipped")]
    public void BatteryUsesTheThresholdInTheRecordedDecision(RuleDecisionReason reason,int? soc,int threshold,string status)
    {
        var condition=RuleEvaluationPresentation.Conditions(Rule,Decision(reason,soc)).Single(c=>c.Kind=="battery_soc");
        Assert.Equal(soc,condition.Observed);Assert.Equal(threshold,condition.Threshold);Assert.Equal(status,condition.Status);
    }

    [Theory]
    [InlineData(RuleDecisionReason.TurnOnConditionsSatisfied,4000,false,"passed")]
    [InlineData(RuleDecisionReason.SolarAverageBelowThreshold,2000,false,"blocked")]
    [InlineData(RuleDecisionReason.SolarAverageUnavailable,null,false,"unknown")]
    [InlineData(RuleDecisionReason.TurnOnConditionsSatisfied,null,true,"bypassed")]
    [InlineData(RuleDecisionReason.Cooldown,4000,false,"skipped")]
    [InlineData(RuleDecisionReason.SocReachedTurnOffThreshold,4000,false,"skipped")]
    public void SolarDistinguishesNotReachedBypassedMissingAndKnownEvidence(RuleDecisionReason reason,int? solar,bool bypass,string status)
    {
        var condition=RuleEvaluationPresentation.Conditions(Rule,Decision(reason,solar:solar,bypass:bypass)).Single(c=>c.Kind=="solar_average");
        Assert.Equal(status,condition.Status);Assert.Equal(solar,condition.Observed);Assert.Equal(3000,condition.Threshold);
    }

    [Theory]
    [InlineData(RuleDecisionReason.Cooldown,4,"blocked")]
    [InlineData(RuleDecisionReason.TurnOnConditionsSatisfied,0,"passed")]
    [InlineData(RuleDecisionReason.MeasurementUnavailable,0,"skipped")]
    [InlineData(RuleDecisionReason.SocBelowTurnOnThreshold,0,"skipped")]
    public void CooldownDoesNotClaimItWasPassedWhenThePolicyStoppedEarlier(RuleDecisionReason reason,double minutes,string status)
        =>Assert.Equal(status,RuleEvaluationPresentation.Conditions(Rule,Decision(reason,cooldown:minutes)).Single(c=>c.Kind=="cooldown").Status);

    [Theory]
    [InlineData(RuleDecisionReason.OutsideActiveWindow,"blocked")]
    [InlineData(RuleDecisionReason.TurnOnConditionsSatisfied,"passed")]
    [InlineData(RuleDecisionReason.Disabled,"skipped")]
    public void ActiveWindowShowsTheRecordedPolicyOutcome(RuleDecisionReason reason,string status)
        =>Assert.Equal(status,RuleEvaluationPresentation.Conditions(Rule,Decision(reason)).Single(c=>c.Kind=="active_window").Status);
}
