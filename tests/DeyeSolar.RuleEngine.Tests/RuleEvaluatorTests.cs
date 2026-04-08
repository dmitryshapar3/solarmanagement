using DeyeSolar.Domain.Models;
using DeyeSolar.RuleEngine;

namespace DeyeSolar.RuleEngine.Tests;

public class RuleEvaluatorTests
{
    private readonly RuleEvaluator _evaluator = new();
    private readonly DateTimeOffset _now = new(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static InverterData MakeData(int soc = 50, int solar = 3500, int batteryPower = 0, DateTimeOffset? ts = null) => new()
    {
        BatterySoc = soc,
        SolarProduction = solar,
        BatteryPower = batteryPower,
        LoadPower = 500,
        BatteryVoltage = 48.0,
        BatteryTemperature = 25.0,
        BatteryCurrent = 0,
        GridConsumption = 0,
        Timestamp = ts ?? DateTimeOffset.UtcNow
    };

    private List<InverterData> MakeSolarReadings(int solar, int minutes) =>
        Enumerable.Range(0, minutes)
            .Select(i => MakeData(solar: solar, ts: _now.AddMinutes(-minutes + i)))
            .ToList();

    private List<InverterData> MakeDischargeReadings(int minutes, int soc = 60, int solar = 3500) =>
        Enumerable.Range(0, minutes)
            .Select(i => MakeData(soc: soc, solar: solar, batteryPower: 500, ts: _now.AddMinutes(-minutes + i)))
            .ToList();

    private static TriggerRule MakeDefaultRule(bool currentState = false) => new()
    {
        Id = 1,
        Name = "Solar Battery Management",
        EntityId = "test",
        Enabled = true,
        SocTurnOnThreshold = 50,
        MinSolarPowerWatts = 3000,
        SolarSustainedMinutes = 30,
        DischargeSustainedMinutes = 5,
        CurrentState = currentState
    };

    // === Turn ON ===

    [Fact]
    public void TurnOn_AllConditionsMet()
    {
        var data = MakeData(soc: 60, solar: 3500);
        var readings = MakeSolarReadings(3500, 35);
        var rule = MakeDefaultRule(currentState: false);

        var actions = _evaluator.Evaluate(data, readings, new[] { rule }, _now);

        Assert.Single(actions);
        Assert.True(actions[0].TurnOn);
    }

    [Fact]
    public void NoTurnOn_SocBelowThreshold()
    {
        var data = MakeData(soc: 40, solar: 3500);
        var readings = MakeSolarReadings(3500, 35);
        var rule = MakeDefaultRule(currentState: false);

        var actions = _evaluator.Evaluate(data, readings, new[] { rule }, _now);

        Assert.Empty(actions);
    }

    [Fact]
    public void NoTurnOn_SolarNotSustained()
    {
        var data = MakeData(soc: 60, solar: 3500);
        var readings = MakeSolarReadings(3500, 20); // only 20 min, need 30
        var rule = MakeDefaultRule(currentState: false);

        var actions = _evaluator.Evaluate(data, readings, new[] { rule }, _now);

        Assert.Empty(actions);
    }

    [Fact]
    public void NoTurnOn_SolarDippedDuringSustainedPeriod()
    {
        var readings = MakeSolarReadings(3500, 30);
        readings[15] = MakeData(solar: 2000, ts: _now.AddMinutes(-15));
        var data = MakeData(soc: 60, solar: 3500);
        var rule = MakeDefaultRule(currentState: false);

        var actions = _evaluator.Evaluate(data, readings, new[] { rule }, _now);

        Assert.Empty(actions);
    }

    // === Turn OFF: conditions lost ===

    [Fact]
    public void TurnOff_SocDroppedBelowThreshold()
    {
        var data = MakeData(soc: 40, solar: 3500);
        var readings = MakeSolarReadings(3500, 35);
        var rule = MakeDefaultRule(currentState: true);

        var actions = _evaluator.Evaluate(data, readings, new[] { rule }, _now);

        Assert.Single(actions);
        Assert.False(actions[0].TurnOn);
    }

    [Fact]
    public void TurnOff_SolarDropped()
    {
        var data = MakeData(soc: 60, solar: 1000);
        var readings = MakeSolarReadings(1000, 35); // solar below threshold
        var rule = MakeDefaultRule(currentState: true);

        var actions = _evaluator.Evaluate(data, readings, new[] { rule }, _now);

        Assert.Single(actions);
        Assert.False(actions[0].TurnOn);
    }

    // === Turn OFF: force-off by sustained discharge ===

    [Fact]
    public void TurnOff_ForceOff_DischargingSustained()
    {
        // ON conditions met, but discharge force-off overrides
        var readings = MakeDischargeReadings(6, soc: 60, solar: 3500);
        var data = MakeData(soc: 60, solar: 3500, batteryPower: 500);
        var rule = MakeDefaultRule(currentState: true);

        var actions = _evaluator.Evaluate(data, readings, new[] { rule }, _now);

        Assert.Single(actions);
        Assert.False(actions[0].TurnOn);
    }

    [Fact]
    public void StaysOn_DischargeNotLongEnough()
    {
        // ON conditions met, discharge too short -- stays ON
        var readings = MakeSolarReadings(3500, 35);
        // Add 3 min of discharge at the end (not enough for 5 min threshold)
        for (int i = 0; i < 3; i++)
            readings.Add(MakeData(soc: 60, solar: 3500, batteryPower: 500, ts: _now.AddMinutes(-3 + i)));
        var data = MakeData(soc: 60, solar: 3500, batteryPower: 500);
        var rule = MakeDefaultRule(currentState: true);

        var actions = _evaluator.Evaluate(data, readings, new[] { rule }, _now);

        Assert.Empty(actions);
    }

    [Fact]
    public void StaysOn_ConditionsMetAndNoDischarge()
    {
        var data = MakeData(soc: 60, solar: 3500, batteryPower: -200); // charging
        var readings = MakeSolarReadings(3500, 35);
        var rule = MakeDefaultRule(currentState: true);

        var actions = _evaluator.Evaluate(data, readings, new[] { rule }, _now);

        Assert.Empty(actions);
    }

    // === Time window ===

    [Fact]
    public void TimeWindow_OutsideWindow_NoAction()
    {
        var data = MakeData(soc: 60, solar: 3500);
        var readings = MakeSolarReadings(3500, 35);
        var rule = MakeDefaultRule(currentState: false);
        rule.ActiveFrom = new TimeOnly(22, 0);
        rule.ActiveTo = new TimeOnly(6, 0);

        var actions = _evaluator.Evaluate(data, readings, new[] { rule }, _now);

        Assert.Empty(actions);
    }

    [Fact]
    public void TimeWindow_InsideWindow_Triggers()
    {
        var data = MakeData(soc: 60, solar: 3500);
        var readings = MakeSolarReadings(3500, 35);
        var rule = MakeDefaultRule(currentState: false);
        rule.ActiveFrom = new TimeOnly(8, 0);
        rule.ActiveTo = new TimeOnly(18, 0);

        var actions = _evaluator.Evaluate(data, readings, new[] { rule }, _now);

        Assert.Single(actions);
    }

    // === Disabled ===

    [Fact]
    public void DisabledRule_Ignored()
    {
        var data = MakeData(soc: 60, solar: 3500);
        var readings = MakeSolarReadings(3500, 35);
        var rule = MakeDefaultRule(currentState: false);
        rule.Enabled = false;

        var actions = _evaluator.Evaluate(data, readings, new[] { rule }, _now);

        Assert.Empty(actions);
    }

    // === SOC-only rule ===

    [Fact]
    public void SocOnlyRule_TurnsOn()
    {
        var data = MakeData(soc: 60);
        var rule = new TriggerRule
        {
            Id = 2, Name = "SOC only", EntityId = "test", Enabled = true,
            SocTurnOnThreshold = 50, CurrentState = false
        };

        var actions = _evaluator.Evaluate(data, Array.Empty<InverterData>(), new[] { rule }, _now);

        Assert.Single(actions);
        Assert.True(actions[0].TurnOn);
    }

    [Fact]
    public void SocOnlyRule_TurnsOff_WhenSocDrops()
    {
        var data = MakeData(soc: 40);
        var rule = new TriggerRule
        {
            Id = 2, Name = "SOC only", EntityId = "test", Enabled = true,
            SocTurnOnThreshold = 50, CurrentState = true
        };

        var actions = _evaluator.Evaluate(data, Array.Empty<InverterData>(), new[] { rule }, _now);

        Assert.Single(actions);
        Assert.False(actions[0].TurnOn);
    }
}
