import type { InverterData } from "../../core/api/types";

export type FlowKind = "solar" | "grid" | "battery" | "load";
export type FlowDirection = "in" | "out" | null;

export function energyFlowReadings(inverter: InverterData | null) {
  const measured = (valid: unknown, watts: unknown, signed = true) => valid === true && typeof watts === "number" && Number.isFinite(watts) && (signed || watts >= 0) ? watts : null;
  return {
    solar: measured(inverter?.solarPowerValid, inverter?.solarProduction, false),
    grid: measured(inverter?.gridPowerValid, inverter?.gridConsumption),
    battery: measured(inverter?.batteryPowerValid, inverter?.batteryPower),
    load: measured(inverter?.loadPowerValid, inverter?.loadPower, false),
    soc: inverter?.batterySocValid === true && Number.isFinite(inverter.batterySoc) && inverter.batterySoc >= 0 && inverter.batterySoc <= 100 ? inverter.batterySoc : null
  };
}

// Positive grid import / battery discharge supply the junction; load consumes it.
export function energyFlowDirection(kind: FlowKind, watts: number | null): FlowDirection {
  if (watts === null || !Number.isFinite(watts) || watts === 0 || ((kind === "solar" || kind === "load") && watts < 0)) return null;
  return kind === "load" || watts < 0 ? "out" : "in";
}
