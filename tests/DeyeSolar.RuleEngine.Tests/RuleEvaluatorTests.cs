using DeyeSolar.Domain.Models;
using DeyeSolar.RuleEngine;

namespace DeyeSolar.RuleEngine.Tests;

public class RuleEvaluatorTests
{
    private readonly RuleEvaluator _evaluator = new();
    private readonly DateTimeOffset _now = new(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static InverterData MakeData(int soc = 90, int batteryPower = 0, int gridConsumption = 0, DateTimeOffset? ts = null) => new()
    {
        BatterySoc = soc,
        BatteryPower = batteryPower,
        GridConsumption = gridConsumption,
        SolarProduction = 3000,
        LoadPower = 500,
        BatteryVoltage = 48.0,
        BatteryTemperature = 25.0,
        BatteryCurrent = 0,
        Timestamp = ts ?? DateTimeOffset.UtcNow
    };

    // Readings with battery+grid draw, spaced 1 min apart
    private List<InverterData> MakeConsumptionReadings(int batteryW, int gridW, int minutes) =>
        Enumerable.Range(0, minutes)
            .Select(i => MakeData(batteryPower: batteryW, gridConsumption: gridW, ts: _now.AddMinutes(-minutes + i)))
            .ToList();

    private static TriggerRule MakeDefaultRule(bool currentState = false) => new()
    {
        Id = 1,
        Name = "Test Rule",
        EntityId = "test-device",
        Enabled = true,
        SocTurnOnThreshold = 80,
        MaxConsumptionWh = 500,
        MonitoringWindowMinutes = 15,
        CooldownMinutes = 15,
        IntervalSeconds = 30,
        CurrentState = currentState
    };

    // === Turn ON ===

    [Fact]
    public void TurnOn_SocAboveThreshold()
    {
        var data = MakeData(soc: 85);
        var rule = MakeDefaultRule(currentState: false);

        var actions = _evaluator.Evaluate(data, [], new[] { rule }, _now);

        Assert.Single(actions);
        Assert.True(actions[0].TurnOn);
    }

    [Fact]
    public void NoTurnOn_SocBelowThreshold()
    {
        var data = MakeData(soc: 70);
        var rule = MakeDefaultRule(currentState: false);

        var actions = _evaluator.Evaluate(data, [], new[] { rule }, _now);

        Assert.Empty(actions);
    }

    [Fact]
    public void NoTurnOn_DuringCooldown()
    {
        var data = MakeData(soc: 85);
        var rule = MakeDefaultRule(currentState: false);
        rule.LastTurnedOff = _now.AddMinutes(-5).DateTime; // 5 min ago, need 15

        var actions = _evaluator.Evaluate(data, [], new[] { rule }, _now);

        Assert.Empty(actions);
    }

    [Fact]
    public void TurnOn_CooldownElapsed()
    {
        var data = MakeData(soc: 85);
        var rule = MakeDefaultRule(currentState: false);
        rule.LastTurnedOff = _now.AddMinutes(-20).DateTime; // 20 min ago, cooldown is 15

        var actions = _evaluator.Evaluate(data, [], new[] { rule }, _now);

        Assert.Single(actions);
        Assert.True(actions[0].TurnOn);
    }

    // === Turn OFF ===

    [Fact]
    public void TurnOff_ConsumptionExceedsThreshold()
    {
        var data = MakeData(soc: 85, batteryPower: 2000, gridConsumption: 500);
        // 2500W total for 15 min = 2500 * 15/60 = 625 Wh > 500 Wh threshold
        var readings = MakeConsumptionReadings(2000, 500, 16);
        var rule = MakeDefaultRule(currentState: true);

        var actions = _evaluator.Evaluate(data, readings, new[] { rule }, _now);

        Assert.Single(actions);
        Assert.False(actions[0].TurnOn);
    }

    [Fact]
    public void StaysOn_ConsumptionBelowThreshold()
    {
        var data = MakeData(soc: 85, batteryPower: 500, gridConsumption: 0);
        // 500W for 15 min = 500 * 15/60 = 125 Wh < 500 Wh threshold
        var readings = MakeConsumptionReadings(500, 0, 16);
        var rule = MakeDefaultRule(currentState: true);

        var actions = _evaluator.Evaluate(data, readings, new[] { rule }, _now);

        Assert.Empty(actions);
    }

    [Fact]
    public void StaysOn_BatteryCharging()
    {
        var data = MakeData(soc: 85, batteryPower: -500, gridConsumption: 0);
        // Negative = charging, max(0, -500) = 0, so no consumption
        var readings = MakeConsumptionReadings(-500, 0, 16);
        var rule = MakeDefaultRule(currentState: true);

        var actions = _evaluator.Evaluate(data, readings, new[] { rule }, _now);

        Assert.Empty(actions);
    }

    [Fact]
    public void StaysOn_NotEnoughReadings()
    {
        var data = MakeData(soc: 85, batteryPower: 5000);
        // Only 1 reading -- not enough to calculate
        var readings = new List<InverterData> { MakeData(batteryPower: 5000, ts: _now) };
        var rule = MakeDefaultRule(currentState: true);

        var actions = _evaluator.Evaluate(data, readings, new[] { rule }, _now);

        Assert.Empty(actions);
    }

    // === Consumption calculation ===

    [Fact]
    public void CalculateConsumption_CorrectWh()
    {
        // 1000W battery + 500W grid = 1500W for 15 min
        // Expected: 1500 * (15/60) = 375 Wh
        var readings = MakeConsumptionReadings(1000, 500, 16);
        var result = RuleEvaluator.CalculateAccumulatedConsumption(readings, 15, _now);

        Assert.InRange(result, 350, 400); // approximate due to interval math
    }

    [Fact]
    public void CalculateConsumption_NegativeValuesIgnored()
    {
        // Battery charging (-500) + grid exporting (-200) = both clamped to 0
        var readings = MakeConsumptionReadings(-500, -200, 16);
        var result = RuleEvaluator.CalculateAccumulatedConsumption(readings, 15, _now);

        Assert.Equal(0, result);
    }

    // === Time window ===

    [Fact]
    public void TimeWindow_OutsideWindow_NoAction()
    {
        var data = MakeData(soc: 85);
        var rule = MakeDefaultRule(currentState: false);
        rule.ActiveFrom = new TimeOnly(22, 0);
        rule.ActiveTo = new TimeOnly(6, 0);

        var actions = _evaluator.Evaluate(data, [], new[] { rule }, _now);

        Assert.Empty(actions);
    }

    [Fact]
    public void TimeWindow_InsideWindow_Triggers()
    {
        var data = MakeData(soc: 85);
        var rule = MakeDefaultRule(currentState: false);
        rule.ActiveFrom = new TimeOnly(8, 0);
        rule.ActiveTo = new TimeOnly(18, 0);

        var actions = _evaluator.Evaluate(data, [], new[] { rule }, _now);

        Assert.Single(actions);
    }

    // === Disabled / no device ===

    [Fact]
    public void DisabledRule_Ignored()
    {
        var data = MakeData(soc: 85);
        var rule = MakeDefaultRule(currentState: false);
        rule.Enabled = false;

        var actions = _evaluator.Evaluate(data, [], new[] { rule }, _now);

        Assert.Empty(actions);
    }
}
