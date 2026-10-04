using DeyeSolar.Domain.Models;
using DeyeSolar.RuleEngine;
using SolarManagement.Inverters.Contracts;

namespace DeyeSolar.RuleEngine.Tests;

public class RuleEvaluatorTests
{
    [Fact]
    public void MissingInverterStillTurnsOffOutsideWindowButNeverTurnsOn()
    {
        var rule = MakeRule(currentState: true);
        rule.ActiveFrom = new TimeOnly(8, 0);
        rule.ActiveTo = new TimeOnly(9, 0);
        var evaluator = new RuleEvaluator();
        var now = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        Assert.False(Assert.Single(evaluator.Evaluate(null, [rule], now)).TurnOn);
        rule.CurrentState = false;
        Assert.Empty(evaluator.Evaluate(null, [rule], now));
        rule.ActiveTo = new TimeOnly(15, 0);
        Assert.Empty(evaluator.Evaluate(null, [rule], now));
    }
    private readonly RuleEvaluator _evaluator = new();
    private readonly DateTimeOffset _now = new(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private InverterData MakeData(int soc = 90) => new()
    {
        BatterySoc = soc,
        BatterySocValid = true,
        Telemetry = new InverterTelemetry(new(Guid.Parse("00000000-0000-0000-0000-000000000001")), _now,
            new(new Percent(soc), _now, MeasurementQuality.Good), new(null, null, MeasurementQuality.Missing),
            new(null, null, MeasurementQuality.Missing), new(null, null, MeasurementQuality.Missing),
            new(null, null, MeasurementQuality.Missing), new(null, null, MeasurementQuality.Missing),
            new(null, null, MeasurementQuality.Missing), new(null, null, MeasurementQuality.Missing), SolarManagement.Inverters.Contracts.SolarPowerBasis.PvDc),
        BatteryPower = 0,
        GridConsumption = 0,
        SolarProduction = 3000,
        LoadPower = 500,
        BatteryVoltage = 48.0,
        BatteryTemperature = 25.0,
        BatteryCurrent = 0,
        Timestamp = _now
    };

    private static TriggerRule MakeRule(bool currentState = false) => new()
    {
        Id = 1,
        Name = "Test Rule",
        EntityId = "test-device",
        Enabled = true,
        SocTurnOnThreshold = 80,
        UseSeparateSocTurnOffThreshold = false,
        SocTurnOffThreshold = 80,
        CooldownMinutes = 15,
        IntervalSeconds = 30,
        CurrentState = currentState
    };

    [Fact]
    public void TurnOn_WhenSocAtThreshold()
    {
        var data = MakeData(soc: 80);
        var rule = MakeRule(currentState: false);

        var actions = _evaluator.Evaluate(data, new[] { rule }, _now);

        Assert.Single(actions);
        Assert.True(actions[0].TurnOn);
    }

    [Theory]
    [InlineData(false, 100)]
    [InlineData(true, 0)]
    public void MissingBatterySocCannotTurnEitherStateIntoAnAuthoritativeDecision(bool currentState, int defaultValue)
    {
        var rule = MakeRule(currentState);
        rule.UseSolarProductionThreshold = true;
        rule.MinAverageSolarProductionWatts = 3000;
        var timestamp = _now.AddMinutes(-20).UtcDateTime;
        rule.CurrentStateChangedAt = timestamp;
        var data = MakeData(defaultValue) with { BatterySocValid = false };
        var actions = _evaluator.Evaluate(data, [rule], _now, context: new(9000));
        Assert.Empty(actions);
        Assert.Equal(currentState, rule.CurrentState);
        Assert.Equal(timestamp, rule.CurrentStateChangedAt);
        Assert.Null(rule.LastEvaluated);
        Assert.Single(_evaluator.Evaluate(data with { BatterySocValid = true }, [rule], _now, context: new(9000)));
    }

    [Fact]
    public void MeasurementsWithoutQualityAndSourceTimeNeverAuthorizeAutomation()
    {
        var rule = MakeRule();
        var reading = MakeData() with { Telemetry = null };
        Assert.False(RuleEvaluator.HasFreshSoc(reading, _now));
        Assert.Empty(_evaluator.Evaluate(reading, [rule], _now));
    }

    [Fact]
    public void NoTurnOn_WhenSocBelowThreshold()
    {
        var data = MakeData(soc: 79);
        var rule = MakeRule(currentState: false);

        var actions = _evaluator.Evaluate(data, new[] { rule }, _now);

        Assert.Empty(actions);
    }

    [Fact]
    public void NoTurnOn_DuringCooldown()
    {
        var data = MakeData(soc: 85);
        var rule = MakeRule(currentState: false);
        rule.CurrentStateChangedAt = _now.AddMinutes(-5).UtcDateTime;

        var actions = _evaluator.Evaluate(data, new[] { rule }, _now);

        Assert.Empty(actions);
    }

    [Fact]
    public void TurnOn_WhenCooldownElapsed()
    {
        var data = MakeData(soc: 85);
        var rule = MakeRule(currentState: false);
        rule.CurrentStateChangedAt = _now.AddMinutes(-20).UtcDateTime;

        var actions = _evaluator.Evaluate(data, new[] { rule }, _now);

        Assert.Single(actions);
        Assert.True(actions[0].TurnOn);
    }

    [Fact]
    public void TurnOn_WhenSolarAverageAtThresholdAndSocBelowBypassThreshold()
    {
        var data = MakeData(soc: 90);
        var rule = MakeRule(currentState: false);
        rule.UseSolarProductionThreshold = true;
        rule.MinAverageSolarProductionWatts = 3000;
        var context = new RuleEvaluationContext(AverageSolarProductionWatts: 3000);

        var actions = _evaluator.Evaluate(data, new[] { rule }, _now, context: context);

        Assert.Single(actions);
        Assert.True(actions[0].TurnOn);
    }

    [Fact]
    public void NoTurnOn_WhenSolarAverageBelowThresholdAndSocBelowBypassThreshold()
    {
        var data = MakeData(soc: 90);
        var rule = MakeRule(currentState: false);
        rule.UseSolarProductionThreshold = true;
        rule.MinAverageSolarProductionWatts = 3000;
        var context = new RuleEvaluationContext(AverageSolarProductionWatts: 2999);

        var actions = _evaluator.Evaluate(data, new[] { rule }, _now, context: context);

        Assert.Empty(actions);
    }

    [Fact]
    public void NoTurnOn_WhenSolarAverageUnavailableAndSocBelowBypassThreshold()
    {
        var data = MakeData(soc: 90);
        var rule = MakeRule(currentState: false);
        rule.UseSolarProductionThreshold = true;
        rule.MinAverageSolarProductionWatts = 3000;
        var context = new RuleEvaluationContext(AverageSolarProductionWatts: null);

        var actions = _evaluator.Evaluate(data, new[] { rule }, _now, context: context);

        Assert.Empty(actions);
    }

    [Fact]
    public void TurnOn_WhenSolarThresholdEnabledAndSocAtBypassThreshold()
    {
        var data = MakeData(soc: 95);
        var rule = MakeRule(currentState: false);
        rule.UseSolarProductionThreshold = true;
        rule.MinAverageSolarProductionWatts = 3000;
        var context = new RuleEvaluationContext(AverageSolarProductionWatts: null);

        var actions = _evaluator.Evaluate(data, new[] { rule }, _now, context: context);

        Assert.Single(actions);
        Assert.True(actions[0].TurnOn);
    }

    [Fact]
    public void StaysOn_WhenInsideTimeWindow()
    {
        var data = MakeData(soc: 90);
        var rule = MakeRule(currentState: true);
        rule.ActiveFrom = new TimeOnly(8, 0);
        rule.ActiveTo = new TimeOnly(18, 0);

        var actions = _evaluator.Evaluate(data, new[] { rule }, _now);

        Assert.Empty(actions);
    }

    [Fact]
    public void TurnOff_WhenSocAtSharedTurnOffThreshold()
    {
        var data = MakeData(soc: 80);
        var rule = MakeRule(currentState: true);

        var actions = _evaluator.Evaluate(data, new[] { rule }, _now);

        Assert.Single(actions);
        Assert.False(actions[0].TurnOn);
    }

    [Fact]
    public void StaysOn_WhenSocAboveSharedTurnOffThreshold()
    {
        var data = MakeData(soc: 81);
        var rule = MakeRule(currentState: true);

        var actions = _evaluator.Evaluate(data, new[] { rule }, _now);

        Assert.Empty(actions);
    }

    [Fact]
    public void TurnOff_WhenSocAtSeparateTurnOffThreshold()
    {
        var data = MakeData(soc: 60);
        var rule = MakeRule(currentState: true);
        rule.UseSeparateSocTurnOffThreshold = true;
        rule.SocTurnOffThreshold = 60;

        var actions = _evaluator.Evaluate(data, new[] { rule }, _now);

        Assert.Single(actions);
        Assert.False(actions[0].TurnOn);
    }

    [Fact]
    public void StaysOn_WhenSocAboveSeparateTurnOffThreshold()
    {
        var data = MakeData(soc: 70);
        var rule = MakeRule(currentState: true);
        rule.UseSeparateSocTurnOffThreshold = true;
        rule.SocTurnOffThreshold = 60;

        var actions = _evaluator.Evaluate(data, new[] { rule }, _now);

        Assert.Empty(actions);
    }

    [Fact]
    public void TimeWindow_OutsideWindow_WhenOff_NoAction()
    {
        var data = MakeData(soc: 85);
        var rule = MakeRule(currentState: false);
        rule.ActiveFrom = new TimeOnly(22, 0);
        rule.ActiveTo = new TimeOnly(6, 0);

        var actions = _evaluator.Evaluate(data, new[] { rule }, _now);

        Assert.Empty(actions);
    }

    [Fact]
    public void TimeWindow_InsideWindow_Triggers()
    {
        var data = MakeData(soc: 85);
        var rule = MakeRule(currentState: false);
        rule.ActiveFrom = new TimeOnly(8, 0);
        rule.ActiveTo = new TimeOnly(18, 0);

        var actions = _evaluator.Evaluate(data, new[] { rule }, _now);

        Assert.Single(actions);
        Assert.True(actions[0].TurnOn);
    }

    [Fact]
    public void TimeWindow_OutsideWindow_WhenOn_ForcesOff()
    {
        var data = MakeData(soc: 85);
        var rule = MakeRule(currentState: true);
        rule.ActiveFrom = new TimeOnly(8, 0);
        rule.ActiveTo = new TimeOnly(10, 0);

        var actions = _evaluator.Evaluate(data, new[] { rule }, _now);

        Assert.Single(actions);
        Assert.False(actions[0].TurnOn);
    }

    [Fact]
    public void TimeWindow_WrappingWindow_OutsideForcesOff()
    {
        var data = MakeData(soc: 85);
        var rule = MakeRule(currentState: true);
        rule.ActiveFrom = new TimeOnly(22, 0);
        rule.ActiveTo = new TimeOnly(6, 0);

        var actions = _evaluator.Evaluate(data, new[] { rule }, _now);

        Assert.Single(actions);
        Assert.False(actions[0].TurnOn);
    }

    [Fact]
    public void DisabledRule_Ignored()
    {
        var data = MakeData(soc: 85);
        var rule = MakeRule(currentState: false);
        rule.Enabled = false;

        var actions = _evaluator.Evaluate(data, new[] { rule }, _now);

        Assert.Empty(actions);
    }
}
