import type { RuleRequest } from "../../core/api/types";
import { validateRuleDraft } from "./RuleDraftPolicy";
export function editorRuleError(draft: RuleRequest, original: RuleRequest | null): { field: keyof RuleRequest; message: string } | null {
  for (const field of ["socTurnOnThreshold", "socTurnOffThreshold", "minAverageSolarProductionWatts", "cooldownMinutes", "intervalSeconds"] as const) {
    if ((field !== "minAverageSolarProductionWatts" || draft.useSolarProductionThreshold) && !Number.isInteger(draft[field])) return { field, message: "Enter a whole number." };
  }
  const changedRange = !original || draft.socTurnOnThreshold !== original.socTurnOnThreshold || draft.socTurnOffThreshold !== original.socTurnOffThreshold || draft.useSeparateSocTurnOffThreshold !== original.useSeparateSocTurnOffThreshold;
  if (changedRange && draft.socTurnOffThreshold >= draft.socTurnOnThreshold) return { field: "socTurnOffThreshold", message: "Turn-off charge must be lower than turn-on charge." };
  const error = validateRuleDraft(draft, true);
  if (!error) return null;
  const fields: Record<string, keyof RuleRequest> = { name_required: "name", target_required: "entityId", soc_on_range: "socTurnOnThreshold", soc_off_range: "socTurnOffThreshold", soc_order: "socTurnOffThreshold", solar_watts_range: "minAverageSolarProductionWatts", cooldown_range: "cooldownMinutes", interval_range: "intervalSeconds", time_window_pair: "activeFrom", time_window_format: "activeFrom" };
  return { field: fields[error.code]!, message: error.message };
}
