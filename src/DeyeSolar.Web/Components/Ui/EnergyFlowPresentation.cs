using DeyeSolar.Domain.Models;

namespace DeyeSolar.Web.Components.Ui;

public sealed record EnergyFlowThreshold(int Id, string Name, int On, int Off);

public static class EnergyFlowPresentation
{
    // The caller scopes rules to the inverter whose battery is displayed.
    public static IReadOnlyList<EnergyFlowThreshold> Thresholds(IEnumerable<TriggerRule> rules) => rules
        .Where(rule => rule.Enabled && rule.SocTurnOnThreshold is >= 0 and <= 100
            && (!rule.UseSeparateSocTurnOffThreshold || rule.SocTurnOffThreshold is >= 0 and <= 100))
        .Select(rule => new EnergyFlowThreshold(rule.Id, rule.Name, rule.SocTurnOnThreshold,
            rule.UseSeparateSocTurnOffThreshold ? rule.SocTurnOffThreshold : rule.SocTurnOnThreshold))
        .OrderBy(rule => rule.Id).ToArray();
}
