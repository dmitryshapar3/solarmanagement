namespace DeyeSolar.Domain.Models;

public sealed record RuleConfigurationError(string Code, string Field, string Message);

/// <summary>Shared configuration defaults and invariants for every editing entry point.</summary>
public static class RuleConfigurationPolicy
{
    public static void Normalize(TriggerRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.EntityId)) rule.Enabled = false;
        if (!rule.UseSeparateSocTurnOffThreshold) rule.SocTurnOffThreshold = rule.SocTurnOnThreshold;
        if (rule.UseSolarProductionThreshold && rule.MinAverageSolarProductionWatts <= 0)
            rule.MinAverageSolarProductionWatts = 3000;
    }

    public static RuleConfigurationError? Validate(TriggerRule rule) => Errors(rule).FirstOrDefault();
    public static bool HasError(TriggerRule rule, params string[] codes) => Errors(rule).Any(error => codes.Contains(error.Code));
    public static IEnumerable<RuleConfigurationError> Errors(TriggerRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Name)) yield return new("name_required", nameof(rule.Name), "Rule name is required.");
        if (rule.SocTurnOnThreshold is < 0 or > 100) yield return new("soc_on_range", nameof(rule.SocTurnOnThreshold), "SOC turn ON must be between 0 and 100%.");
        if (rule.UseSeparateSocTurnOffThreshold)
        {
            if (rule.SocTurnOffThreshold is < 0 or > 100) yield return new("soc_off_range", nameof(rule.SocTurnOffThreshold), "SOC turn OFF must be between 0 and 100%.");
            if (rule.SocTurnOffThreshold > rule.SocTurnOnThreshold) yield return new("soc_order", nameof(rule.SocTurnOffThreshold), "SOC turn OFF cannot be higher than SOC turn ON.");
        }
        if (rule.UseSolarProductionThreshold && rule.MinAverageSolarProductionWatts is < 1 or > 30000)
            yield return new("solar_watts_range", nameof(rule.MinAverageSolarProductionWatts), "Average PV threshold must be between 1 and 30000 W.");
        if (rule.CooldownMinutes is < 1 or > 240) yield return new("cooldown_range", nameof(rule.CooldownMinutes), "Cooldown must be between 1 and 240 minutes.");
        if (rule.IntervalSeconds is < 10 or > 3600) yield return new("interval_range", nameof(rule.IntervalSeconds), "Evaluation interval must be between 10 and 3600 seconds.");
        if (rule.ActiveFrom.HasValue != rule.ActiveTo.HasValue) yield return new("time_window_pair", nameof(rule.ActiveFrom), "Set both time-window values or leave both empty.");
    }
}
