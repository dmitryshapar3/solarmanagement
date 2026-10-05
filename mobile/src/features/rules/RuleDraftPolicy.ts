import type { RuleRequest } from "../../core/api/types";

export type RuleDraftErrorCode = "name_required" | "target_required" | "soc_on_range" | "soc_off_range"
  | "soc_order" | "solar_watts_range" | "cooldown_range" | "interval_range" | "time_window_pair" | "time_window_format";
export type RuleDraftError = { code: RuleDraftErrorCode; message: string };

/** Configuration defaults match the authoritative server policy. Runtime state never enters a draft. */
export function normalizeRuleDraft(rule: RuleRequest): RuleRequest {
  const entityId = rule.entityId.trim();
  return {
    name: rule.name.trim(), entityId, sourceInverterId: rule.sourceInverterId,
    enabled: Boolean(entityId) && rule.enabled,
    socTurnOnThreshold: rule.socTurnOnThreshold,
    useSeparateSocTurnOffThreshold: rule.useSeparateSocTurnOffThreshold,
    socTurnOffThreshold: rule.useSeparateSocTurnOffThreshold ? rule.socTurnOffThreshold : rule.socTurnOnThreshold,
    useSolarProductionThreshold: rule.useSolarProductionThreshold,
    minAverageSolarProductionWatts: rule.useSolarProductionThreshold && rule.minAverageSolarProductionWatts <= 0
      ? 3000 : rule.minAverageSolarProductionWatts,
    cooldownMinutes: rule.cooldownMinutes, intervalSeconds: rule.intervalSeconds,
    activeFrom: rule.activeFrom?.trim() || null, activeTo: rule.activeTo?.trim() || null,
    ...(rule.configurationVersion === undefined ? {} : { configurationVersion: rule.configurationVersion })
  };
}

export function validateRuleDraft(rule: RuleRequest, requireTarget = false): RuleDraftError | null {
  const error = (code: RuleDraftErrorCode, message: string): RuleDraftError => ({ code, message });
  if (!rule.name.trim()) return error("name_required", "Rule name is required.");
  if (requireTarget && !rule.entityId.trim()) return error("target_required", "Select a target device.");
  if (rule.socTurnOnThreshold < 0 || rule.socTurnOnThreshold > 100)
    return error("soc_on_range", "SOC turn ON must be between 0 and 100%.");
  if (rule.useSeparateSocTurnOffThreshold) {
    if (rule.socTurnOffThreshold < 0 || rule.socTurnOffThreshold > 100)
      return error("soc_off_range", "SOC turn OFF must be between 0 and 100%.");
    if (rule.socTurnOffThreshold > rule.socTurnOnThreshold)
      return error("soc_order", "SOC turn OFF cannot be higher than SOC turn ON.");
  }
  if (rule.useSolarProductionThreshold && (rule.minAverageSolarProductionWatts < 1 || rule.minAverageSolarProductionWatts > 30000))
    return error("solar_watts_range", "Average PV threshold must be between 1 and 30000 W.");
  if (rule.cooldownMinutes < 1 || rule.cooldownMinutes > 240)
    return error("cooldown_range", "Cooldown must be between 1 and 240 minutes.");
  if (rule.intervalSeconds < 10 || rule.intervalSeconds > 3600)
    return error("interval_range", "Interval must be between 10 and 3600 seconds.");
  if (Boolean(rule.activeFrom) !== Boolean(rule.activeTo))
    return error("time_window_pair", "Set both time-window values or leave both empty.");
  const timePattern = /^([01]\d|2[0-3]):[0-5]\d$/;
  if (rule.activeFrom && !timePattern.test(rule.activeFrom) || rule.activeTo && !timePattern.test(rule.activeTo))
    return error("time_window_format", "Time window values must use HH:mm.");
  return null;
}
