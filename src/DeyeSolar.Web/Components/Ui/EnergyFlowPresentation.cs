using DeyeSolar.Domain.Models;

namespace DeyeSolar.Web.Components.Ui;

public sealed record EnergyFlowThreshold(int Id, string Name, int On, int Off);

public sealed record EnergyFlowConnection(string Key, string Transform, int Length, double? Kilowatts, bool PositiveTowardJunction = true)
{
    public bool Active => Kilowatts is { } value && double.IsFinite(value) && value != 0;
    public bool Reverse => Active && (Kilowatts > 0 != PositiveTowardJunction);
    public string State => Active ? "active" : Kilowatts is { } value && double.IsFinite(value) ? "idle" : "unknown";
    public string Direction => Active ? Reverse ? "away-from-junction" : "toward-junction" : "none";
}

public static class EnergyFlowPresentation
{
    // Each local path starts at its outer node and runs toward the junction.
    public static IReadOnlyList<EnergyFlowConnection> Connections(LiveReading data) =>
    [
        new("solar", "translate(180 100) rotate(90)", 35, NonnegativePower(data.SolarKw)),
        new("battery", "translate(99 148)", 67, FinitePower(data.BatteryKw)),
        new("grid", "translate(261 148) rotate(180)", 67, FinitePower(data.GridKw)),
        new("load", "translate(180 215) rotate(-90)", 53, NonnegativePower(data.LoadKw), PositiveTowardJunction: false)
    ];

    public static double? FinitePower(double? value) => value is { } power && double.IsFinite(power) ? power : null;
    public static double? NonnegativePower(double? value) => value is { } power && double.IsFinite(power) && power >= 0 ? power : null;

    // The caller scopes rules to the inverter whose battery is displayed.
    public static IReadOnlyList<EnergyFlowThreshold> Thresholds(IEnumerable<TriggerRule> rules) => rules
        .Where(rule => rule.Enabled && rule.SocTurnOnThreshold is >= 0 and <= 100
            && (!rule.UseSeparateSocTurnOffThreshold || rule.SocTurnOffThreshold is >= 0 and <= 100))
        .Select(rule => new EnergyFlowThreshold(rule.Id, rule.Name, rule.SocTurnOnThreshold,
            rule.UseSeparateSocTurnOffThreshold ? rule.SocTurnOffThreshold : rule.SocTurnOnThreshold))
        .OrderBy(rule => rule.Id).ToArray();
}
