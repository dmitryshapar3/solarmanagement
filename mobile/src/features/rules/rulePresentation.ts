export function ruleDecisionLabel(reason: string): string {
  const labels: Record<string, string> = { Disabled: "Automation paused", OutsideActiveWindow: "Outside active hours", MeasurementUnavailable: "Waiting for a recent reading", SocBelowTurnOnThreshold: "Waiting for battery charge", SocReachedTurnOffThreshold: "Battery reached the turn-off threshold", RemainOn: "Conditions keep the device on", Cooldown: "Waiting between switches", SolarAverageUnavailable: "Waiting for solar measurements", SolarAverageBelowThreshold: "Waiting for more solar", TurnOnConditionsSatisfied: "Turn-on conditions met", not_checked: "No check recorded", sample_data: "Sample check" };
  return labels[reason] ?? "Check details unavailable";
}
