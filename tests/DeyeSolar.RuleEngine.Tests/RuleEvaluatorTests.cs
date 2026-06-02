using DeyeSolar.Domain.Models;
using DeyeSolar.RuleEngine;

namespace DeyeSolar.RuleEngine.Tests;

public class RuleEvaluatorTests
{
    private readonly RuleEvaluator _evaluator = new();
    private readonly DateTimeOffset _now = new(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private InverterData MakeData(int soc = 90) => new()
    {
        BatterySoc = soc,
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
