using DeyeSolar.Domain.Models;
using DeyeSolar.RuleEngine;

namespace DeyeSolar.RuleEngine.Tests;

public class RuleEvaluatorTests
{
    private readonly RuleEvaluator _evaluator = new();
    private readonly DateTimeOffset _now = new(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static InverterData MakeData(int soc = 50, int solar = 1000, int load = 500) => new()
    {
        BatterySoc = soc,
        SolarProduction = solar,
        LoadPower = load,
        BatteryPower = 0,
        BatteryVoltage = 48.0,
        BatteryTemperature = 25.0,
        BatteryCurrent = 0,
        GridConsumption = 0,
        Timestamp = DateTimeOffset.UtcNow
    };

    private static TriggerRule MakeSocRule(int onAt = 80, int offAt = 60, bool currentState = false) => new()
    {
        Id = 1,
        Name = "Test SOC Rule",
        EntityId = "switch.test",
        Enabled = true,
        SocTurnOnThreshold = onAt,
        SocTurnOffThreshold = offAt,
        CooldownSeconds = 0,
        CurrentState = currentState
    };

    [Fact]
    public void SocAboveOnThreshold_TurnsOn()
    {
        var data = MakeData(soc: 85);
        var rule = MakeSocRule(onAt: 80, offAt: 60, currentState: false);

        var actions = _evaluator.Evaluate(data, new[] { rule }, _now);

        Assert.Single(actions);
        Assert.True(actions[0].TurnOn);
    }

    [Fact]
    public void SocBelowOnThreshold_NoAction()
    {
        var data = MakeData(soc: 70);
        var rule = MakeSocRule(onAt: 80, offAt: 60, currentState: false);

        var actions = _evaluator.Evaluate(data, new[] { rule }, _now);

        Assert.Empty(actions);
    }

    [Fact]
    public void SocAboveOffThreshold_StaysOn()
    {
        var data = MakeData(soc: 70); // between 60 and 80
        var rule = MakeSocRule(onAt: 80, offAt: 60, currentState: true);

        var actions = _evaluator.Evaluate(data, new[] { rule }, _now);

        Assert.Empty(actions); // no change needed
    }

    [Fact]
    public void SocDropsBelowOffThreshold_TurnsOff()
    {
        var data = MakeData(soc: 55);
        var rule = MakeSocRule(onAt: 80, offAt: 60, currentState: true);

        var actions = _evaluator.Evaluate(data, new[] { rule }, _now);

        Assert.Single(actions);
        Assert.False(actions[0].TurnOn);
    }

    [Fact]
    public void SolarSurplus_TurnsOn()
    {
        var data = MakeData(solar: 2000, load: 500);
        var rule = new TriggerRule
        {
            Id = 2, Name = "Surplus", EntityId = "switch.test", Enabled = true,
            MinSolarSurplusWatts = 1000, CooldownSeconds = 0, CurrentState = false
        };

        var actions = _evaluator.Evaluate(data, new[] { rule }, _now);

        Assert.Single(actions);
        Assert.True(actions[0].TurnOn);
    }

    [Fact]
    public void InsufficientSurplus_NoAction()
    {
        var data = MakeData(solar: 800, load: 500);
        var rule = new TriggerRule
        {
            Id = 2, Name = "Surplus", EntityId = "switch.test", Enabled = true,
            MinSolarSurplusWatts = 1000, CooldownSeconds = 0, CurrentState = false
        };

        var actions = _evaluator.Evaluate(data, new[] { rule }, _now);

        Assert.Empty(actions);
    }

    [Fact]
    public void DisabledRule_Ignored()
    {
        var data = MakeData(soc: 90);
        var rule = MakeSocRule(currentState: false);
        rule.Enabled = false;

        var actions = _evaluator.Evaluate(data, new[] { rule }, _now);

        Assert.Empty(actions);
    }

    [Fact]
    public void Cooldown_PreventsAction()
    {
        var data = MakeData(soc: 90);
        var rule = MakeSocRule(currentState: false);
        rule.CooldownSeconds = 120;
        rule.LastTriggered = _now.AddSeconds(-60); // 60s ago, within 120s cooldown

        var actions = _evaluator.Evaluate(data, new[] { rule }, _now);

        Assert.Empty(actions);
    }

    [Fact]
    public void CooldownExpired_AllowsAction()
    {
        var data = MakeData(soc: 90);
        var rule = MakeSocRule(currentState: false);
        rule.CooldownSeconds = 60;
        rule.LastTriggered = _now.AddSeconds(-120); // 120s ago, past 60s cooldown

        var actions = _evaluator.Evaluate(data, new[] { rule }, _now);

        Assert.Single(actions);
    }

    [Fact]
    public void TimeWindow_OutsideWindow_NoAction()
    {
        var data = MakeData(soc: 90);
        var rule = MakeSocRule(currentState: false);
        rule.ActiveFrom = new TimeOnly(22, 0); // 10 PM
        rule.ActiveTo = new TimeOnly(6, 0);    // 6 AM
        // _now is 12:00 noon, outside this window

        var actions = _evaluator.Evaluate(data, new[] { rule }, _now);

        Assert.Empty(actions);
    }

    [Fact]
    public void TimeWindow_InsideWindow_Triggers()
    {
        var data = MakeData(soc: 90);
        var rule = MakeSocRule(currentState: false);
        rule.ActiveFrom = new TimeOnly(8, 0);  // 8 AM
        rule.ActiveTo = new TimeOnly(18, 0);   // 6 PM
        // _now is 12:00 noon, inside this window

        var actions = _evaluator.Evaluate(data, new[] { rule }, _now);

        Assert.Single(actions);
    }

    [Fact]
    public void CombinedConditions_AllMustMatch()
    {
        var data = MakeData(soc: 90, solar: 2000, load: 500);
        var rule = MakeSocRule(onAt: 80, offAt: 60, currentState: false);
        rule.MinSolarSurplusWatts = 1000;

        var actions = _evaluator.Evaluate(data, new[] { rule }, _now);

        Assert.Single(actions);
        Assert.True(actions[0].TurnOn);
    }

    [Fact]
    public void CombinedConditions_SocMetButSurplusNot_NoAction()
    {
        var data = MakeData(soc: 90, solar: 800, load: 500);
        var rule = MakeSocRule(onAt: 80, offAt: 60, currentState: false);
        rule.MinSolarSurplusWatts = 1000;

        var actions = _evaluator.Evaluate(data, new[] { rule }, _now);

        Assert.Empty(actions);
    }
}
