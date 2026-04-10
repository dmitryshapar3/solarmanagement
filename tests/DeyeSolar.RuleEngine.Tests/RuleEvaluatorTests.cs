using DeyeSolar.Domain.Models;
using DeyeSolar.RuleEngine;

namespace DeyeSolar.RuleEngine.Tests;

public class RuleEvaluatorTests
{
    private readonly RuleEvaluator _evaluator = new();
    private readonly DateTimeOffset _now = new(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private InverterData MakeData(int soc = 90, int batteryPower = 0, DateTimeOffset? ts = null) => new()
    {
        BatterySoc = soc,
        BatteryPower = batteryPower,
        GridConsumption = 0,
        SolarProduction = 3000,
        LoadPower = 500,
        BatteryVoltage = 48.0,
        BatteryTemperature = 25.0,
        BatteryCurrent = 0,
        Timestamp = ts ?? _now
    };

    // Readings spaced 1 min apart covering `minutes` minutes, ending at _now
    private List<InverterData> MakeReadings(int batteryPower, int minutes, int soc = 90) =>
        Enumerable.Range(0, minutes + 1)
            .Select(i => MakeData(soc: soc, batteryPower: batteryPower, ts: _now.AddMinutes(-minutes + i)))
            .ToList();

    private static TriggerRule MakeRule(bool currentState = false) => new()
    {
        Id = 1,
        Name = "Test Rule",
        EntityId = "test-device",
        Enabled = true,
        SocTurnOnThreshold = 80,
        SocFloor = 55,
        MaxDrainWh = 200,
        DrainWindowMinutes = 15,
        MaxSocDropPercent = 100, // disabled by default for tests that don't exercise it
        MinOnMinutes = 10,
        CooldownMinutes = 15,
        IntervalSeconds = 30,
        CurrentState = currentState
    };

    // === Turn ON ===

    [Fact]
    public void TurnOn_WhenSocAtThreshold()
    {
        var data = MakeData(soc: 80);
        var rule = MakeRule(currentState: false);

        var actions = _evaluator.Evaluate(data, [], new[] { rule }, _now);

        Assert.Single(actions);
        Assert.True(actions[0].TurnOn);
    }

    [Fact]
    public void NoTurnOn_WhenSocBelowThreshold()
    {
        var data = MakeData(soc: 79);
        var rule = MakeRule(currentState: false);

        var actions = _evaluator.Evaluate(data, [], new[] { rule }, _now);

        Assert.Empty(actions);
    }

    [Fact]
    public void NoTurnOn_DuringCooldown()
    {
        var data = MakeData(soc: 85);
        var rule = MakeRule(currentState: false);
        rule.CurrentStateChangedAt = _now.AddMinutes(-5).UtcDateTime; // 5 min ago, cooldown is 15

        var actions = _evaluator.Evaluate(data, [], new[] { rule }, _now);

        Assert.Empty(actions);
    }

    [Fact]
    public void TurnOn_WhenCooldownElapsed()
    {
        var data = MakeData(soc: 85);
        var rule = MakeRule(currentState: false);
        rule.CurrentStateChangedAt = _now.AddMinutes(-20).UtcDateTime; // past cooldown

        var actions = _evaluator.Evaluate(data, [], new[] { rule }, _now);

        Assert.Single(actions);
        Assert.True(actions[0].TurnOn);
    }

    [Fact]
    public void NoTurnOn_WhenSocAtFloor()
    {
        // SOC above turn-on threshold but at or below the safety floor — should not turn on
        var rule = MakeRule(currentState: false);
        rule.SocTurnOnThreshold = 50;
        rule.SocFloor = 55;
        var data = MakeData(soc: 55);

        var actions = _evaluator.Evaluate(data, [], new[] { rule }, _now);

        Assert.Empty(actions);
    }

    // === Turn OFF: drain ===

    [Fact]
    public void TurnOff_WhenNetDrainExceedsThreshold()
    {
        // 1200W sustained discharge for 15 min = 300 Wh net drain > 200 Wh threshold
        var readings = MakeReadings(batteryPower: 1200, minutes: 15);
        var data = MakeData(soc: 75, batteryPower: 1200);

        var rule = MakeRule(currentState: true);
        rule.CurrentStateChangedAt = _now.AddMinutes(-30).UtcDateTime; // past min-on

        var actions = _evaluator.Evaluate(data, readings, new[] { rule }, _now);

        Assert.Single(actions);
        Assert.False(actions[0].TurnOn);
    }

    [Fact]
    public void StaysOn_WhenNetDrainBelowThreshold()
    {
        // 600W sustained for 15 min = 150 Wh < 200 Wh threshold
        var readings = MakeReadings(batteryPower: 600, minutes: 15);
        var data = MakeData(soc: 75, batteryPower: 600);

        var rule = MakeRule(currentState: true);
        rule.CurrentStateChangedAt = _now.AddMinutes(-30).UtcDateTime;

        var actions = _evaluator.Evaluate(data, readings, new[] { rule }, _now);

        Assert.Empty(actions);
    }

    [Fact]
    public void StaysOn_WhenBatteryCharging()
    {
        // Net charging (-1000W) over window — net drain is strongly negative, far below threshold
        var readings = MakeReadings(batteryPower: -1000, minutes: 15);
        var data = MakeData(soc: 90, batteryPower: -1000);

        var rule = MakeRule(currentState: true);
        rule.CurrentStateChangedAt = _now.AddMinutes(-30).UtcDateTime;

        var actions = _evaluator.Evaluate(data, readings, new[] { rule }, _now);

        Assert.Empty(actions);
    }

    [Fact]
    public void StaysOn_WhenCloudTransientIsRecovered()
    {
        // 5 min of 1500W discharge (cloud) = 125 Wh, then 10 min of -1500W charge = -250 Wh.
        // Net drain over the 15-min window should be ~-125 Wh (net charge). Below 200 Wh threshold.
        var readings = new List<InverterData>();
        for (int i = 0; i <= 5; i++)
            readings.Add(MakeData(soc: 78, batteryPower: 1500, ts: _now.AddMinutes(-15 + i)));
        for (int i = 1; i <= 10; i++)
            readings.Add(MakeData(soc: 80, batteryPower: -1500, ts: _now.AddMinutes(-10 + i)));

        var data = MakeData(soc: 80, batteryPower: -1500);
        var rule = MakeRule(currentState: true);
        rule.CurrentStateChangedAt = _now.AddMinutes(-30).UtcDateTime;

        var actions = _evaluator.Evaluate(data, readings, new[] { rule }, _now);

        Assert.Empty(actions);
    }

    // === Turn OFF: SOC floor override ===

    [Fact]
    public void TurnOff_WhenSocAtFloor_OverridesMinOn()
    {
        // Just turned on 1 min ago (well within 10-min MinOnMinutes), but SOC hit the floor.
        // SOC floor must override the hold.
        var data = MakeData(soc: 55, batteryPower: 0);
        var rule = MakeRule(currentState: true);
        rule.CurrentStateChangedAt = _now.AddMinutes(-1).UtcDateTime;

        var actions = _evaluator.Evaluate(data, [], new[] { rule }, _now);

        Assert.Single(actions);
        Assert.False(actions[0].TurnOn);
    }

    [Fact]
    public void TurnOff_WhenSocBelowFloor()
    {
        var data = MakeData(soc: 50, batteryPower: 0);
        var rule = MakeRule(currentState: true);
        rule.CurrentStateChangedAt = _now.AddMinutes(-30).UtcDateTime;

        var actions = _evaluator.Evaluate(data, [], new[] { rule }, _now);

        Assert.Single(actions);
        Assert.False(actions[0].TurnOn);
    }

    // === Turn OFF: MinOnMinutes ===

    [Fact]
    public void StaysOn_WithinMinOnWindow_EvenWithHighDrain()
    {
        // Very high drain (3000W over 15 min = 750 Wh), but device just turned on 5 min ago.
        // MinOnMinutes=10 must hold it ON.
        var readings = MakeReadings(batteryPower: 3000, minutes: 15);
        var data = MakeData(soc: 75, batteryPower: 3000);

        var rule = MakeRule(currentState: true);
        rule.CurrentStateChangedAt = _now.AddMinutes(-5).UtcDateTime;

        var actions = _evaluator.Evaluate(data, readings, new[] { rule }, _now);

        Assert.Empty(actions);
    }

    [Fact]
    public void TurnOff_WhenMinOnElapsed_AndDrainHigh()
    {
        var readings = MakeReadings(batteryPower: 3000, minutes: 15);
        var data = MakeData(soc: 75, batteryPower: 3000);

        var rule = MakeRule(currentState: true);
        rule.CurrentStateChangedAt = _now.AddMinutes(-11).UtcDateTime; // past min-on

        var actions = _evaluator.Evaluate(data, readings, new[] { rule }, _now);

        Assert.Single(actions);
        Assert.False(actions[0].TurnOn);
    }

    // === Drain calculation ===

    [Fact]
    public void CalculateNetDrain_SustainedDischarge()
    {
        // 1000W for 15 min = 250 Wh
        var readings = MakeReadings(batteryPower: 1000, minutes: 15);
        var result = RuleEvaluator.CalculateNetBatteryDrainWh(readings, 15, _now);

        Assert.InRange(result, 240, 260);
    }

    [Fact]
    public void CalculateNetDrain_ChargingIsNegative()
    {
        var readings = MakeReadings(batteryPower: -1000, minutes: 15);
        var result = RuleEvaluator.CalculateNetBatteryDrainWh(readings, 15, _now);

        Assert.InRange(result, -260, -240);
    }

    [Fact]
    public void CalculateNetDrain_NoReadings_ReturnsZero()
    {
        var result = RuleEvaluator.CalculateNetBatteryDrainWh([], 15, _now);
        Assert.Equal(0, result);
    }

    [Fact]
    public void CalculateNetDrain_SingleReading_ReturnsZero()
    {
        var readings = new List<InverterData> { MakeData(batteryPower: 1000, ts: _now) };
        var result = RuleEvaluator.CalculateNetBatteryDrainWh(readings, 15, _now);
        Assert.Equal(0, result);
    }

    // === Time window ===

    [Fact]
    public void TimeWindow_OutsideWindow_NoAction()
    {
        var data = MakeData(soc: 85);
        var rule = MakeRule(currentState: false);
        rule.ActiveFrom = new TimeOnly(22, 0);
        rule.ActiveTo = new TimeOnly(6, 0);

        var actions = _evaluator.Evaluate(data, [], new[] { rule }, _now);

        Assert.Empty(actions);
    }

    [Fact]
    public void TimeWindow_InsideWindow_Triggers()
    {
        var data = MakeData(soc: 85);
        var rule = MakeRule(currentState: false);
        rule.ActiveFrom = new TimeOnly(8, 0);
        rule.ActiveTo = new TimeOnly(18, 0);

        var actions = _evaluator.Evaluate(data, [], new[] { rule }, _now);

        Assert.Single(actions);
    }

    // === Disabled ===

    [Fact]
    public void DisabledRule_Ignored()
    {
        var data = MakeData(soc: 85);
        var rule = MakeRule(currentState: false);
        rule.Enabled = false;

        var actions = _evaluator.Evaluate(data, [], new[] { rule }, _now);

        Assert.Empty(actions);
    }

    // === Drain-episode anchor (MaxSocDropPercent) ===

    [Fact]
    public void DrainAnchor_CapturedOnFirstDrainTick()
    {
        var data = MakeData(soc: 85, batteryPower: 300); // draining
        var rule = MakeRule(currentState: true);
        rule.MaxSocDropPercent = 5;
        rule.CurrentStateChangedAt = _now.AddMinutes(-30).UtcDateTime; // past min-on
        Assert.Null(rule.SocAtDrainStart);

        _evaluator.Evaluate(data, [], new[] { rule }, _now);

        Assert.Equal(85, rule.SocAtDrainStart);
    }

    [Fact]
    public void DrainAnchor_NotCapturedWhenCharging()
    {
        var data = MakeData(soc: 85, batteryPower: -300); // charging
        var rule = MakeRule(currentState: true);
        rule.MaxSocDropPercent = 5;
        rule.CurrentStateChangedAt = _now.AddMinutes(-30).UtcDateTime;

        _evaluator.Evaluate(data, [], new[] { rule }, _now);

        Assert.Null(rule.SocAtDrainStart);
    }

    [Fact]
    public void DrainAnchor_ClearedWhenChargingResumes()
    {
        var data = MakeData(soc: 86, batteryPower: -300); // charging now
        var rule = MakeRule(currentState: true);
        rule.MaxSocDropPercent = 5;
        rule.SocAtDrainStart = 85; // had an anchor from before
        rule.CurrentStateChangedAt = _now.AddMinutes(-30).UtcDateTime;

        _evaluator.Evaluate(data, [], new[] { rule }, _now);

        Assert.Null(rule.SocAtDrainStart);
    }

    [Fact]
    public void DrainAnchor_PreservedWhenIdle()
    {
        var data = MakeData(soc: 85, batteryPower: 0); // idle
        var rule = MakeRule(currentState: true);
        rule.MaxSocDropPercent = 5;
        rule.SocAtDrainStart = 85;
        rule.CurrentStateChangedAt = _now.AddMinutes(-30).UtcDateTime;

        _evaluator.Evaluate(data, [], new[] { rule }, _now);

        Assert.Equal(85, rule.SocAtDrainStart);
    }

    [Fact]
    public void TurnOff_WhenSocDropExceedsThreshold()
    {
        // MaxSocDropPercent = 1, anchor at 85, now at 84 → drop = 1 → >= 1 → turn off
        var data = MakeData(soc: 84, batteryPower: 500); // still draining
        var rule = MakeRule(currentState: true);
        rule.MaxSocDropPercent = 1;
        rule.SocAtDrainStart = 85;
        rule.CurrentStateChangedAt = _now.AddMinutes(-30).UtcDateTime;

        var actions = _evaluator.Evaluate(data, [], new[] { rule }, _now);

        Assert.Single(actions);
        Assert.False(actions[0].TurnOn);
    }

    [Fact]
    public void StaysOn_WhenSocDropUnderThreshold()
    {
        // MaxSocDropPercent = 5, anchor at 85, now at 83 → drop = 2 → under threshold
        var data = MakeData(soc: 83, batteryPower: 500);
        var rule = MakeRule(currentState: true);
        rule.MaxSocDropPercent = 5;
        rule.SocAtDrainStart = 85;
        rule.CurrentStateChangedAt = _now.AddMinutes(-30).UtcDateTime;

        var actions = _evaluator.Evaluate(data, [], new[] { rule }, _now);

        Assert.Empty(actions);
    }

    [Fact]
    public void DrainAnchor_ReCaptureAfterChargeThenDrain()
    {
        // 1. Charging → anchor should be cleared
        var charging = MakeData(soc: 90, batteryPower: -500);
        var rule = MakeRule(currentState: true);
        rule.MaxSocDropPercent = 2;
        rule.SocAtDrainStart = 85; // stale anchor from an earlier episode
        rule.CurrentStateChangedAt = _now.AddMinutes(-30).UtcDateTime;

        _evaluator.Evaluate(charging, [], new[] { rule }, _now);
        Assert.Null(rule.SocAtDrainStart);

        // 2. Now battery is at 90% and draining → new anchor captured at 90
        var draining = MakeData(soc: 90, batteryPower: 500);
        _evaluator.Evaluate(draining, [], new[] { rule }, _now);
        Assert.Equal(90, rule.SocAtDrainStart);

        // 3. SOC drops to 88 → drop = 2 → triggers turn-off
        var dropped = MakeData(soc: 88, batteryPower: 500);
        var actions = _evaluator.Evaluate(dropped, [], new[] { rule }, _now);

        Assert.Single(actions);
        Assert.False(actions[0].TurnOn);
    }

    [Fact]
    public void DrainAnchor_NotChecked_DuringMinOnHold()
    {
        // Just turned on 1 min ago with 10-min MinOn; drop already exceeds threshold.
        // MinOn should hold it ON (but anchor is still captured for later).
        var data = MakeData(soc: 80, batteryPower: 500);
        var rule = MakeRule(currentState: true);
        rule.MaxSocDropPercent = 1;
        rule.SocAtDrainStart = 85;
        rule.CurrentStateChangedAt = _now.AddMinutes(-1).UtcDateTime;

        var actions = _evaluator.Evaluate(data, [], new[] { rule }, _now);

        Assert.Empty(actions);
    }
}
