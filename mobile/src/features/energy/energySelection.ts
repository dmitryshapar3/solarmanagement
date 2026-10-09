import type { ExportSalesPeriod, SolarHistoryPeriod } from "../../core/api/types";
import { addDays, dateCaption, movePeriod } from "./chartPolicy";

export type EnergyPeriod = "Day" | "Week" | "RollingMonth" | "CalendarMonth" | "Custom";
export type EnergySelection = { period: EnergyPeriod; date?: string; from?: string; through?: string };
export const energyPeriodChoices: { period: EnergyPeriod; label: string }[] = [
  { period: "Day", label: "Day" }, { period: "Week", label: "7 days" },
  { period: "RollingMonth", label: "30 days" }, { period: "CalendarMonth", label: "Month" },
  { period: "Custom", label: "Custom" }
];

export function energyWindow(selection: EnergySelection, today: string) {
  const date = selection.date ?? today;
  if (selection.period === "CalendarMonth") {
    const from = `${date.slice(0, 7)}-01`;
    return { date, from, through: addDays(movePeriod(from, "Month", 1), -1) };
  }
  if (selection.period === "Custom") return { date, from: selection.from ?? date, through: selection.through ?? date };
  return { date, from: addDays(date, selection.period === "Week" ? -6 : selection.period === "RollingMonth" ? -29 : 0), through: date };
}

export function energySelectionError(selection: EnergySelection, today: string): string | null {
  try {
    const { date, from, through } = energyWindow(selection, today);
    for (const value of [date, from, through]) addDays(value, 0);
    if (from >= "2000-01-01" && through >= from && through <= addDays(today, 366)
      && Date.parse(`${through}T12:00:00Z`) - Date.parse(`${from}T12:00:00Z`) <= 365 * 86400000) return null;
  } catch { /* Invalid typed calendar dates remain in the editor for correction. */ }
  return "Choose a valid period of at most 366 days.";
}

export function chooseEnergyPeriod(selection: EnergySelection, period: EnergyPeriod, today: string): EnergySelection {
  const current = energyWindow(selection, today);
  return period === "Custom" ? { period, date: current.from, from: current.from, through: current.through }
    : { period, date: period === "CalendarMonth" ? `${current.date.slice(0, 7)}-01` : current.date };
}

export function moveEnergySelection(selection: EnergySelection, today: string, direction: number): EnergySelection {
  const current = energyWindow(selection, today);
  if (selection.period === "CalendarMonth") return { period: selection.period, date: movePeriod(current.date, "Month", direction) };
  const days = selection.period === "Custom" ? Math.round((Date.parse(`${current.through}T12:00:00Z`) - Date.parse(`${current.from}T12:00:00Z`)) / 86400000) + 1
    : selection.period === "Week" ? 7 : selection.period === "RollingMonth" ? 30 : 1;
  return selection.period === "Custom"
    ? { period: selection.period, date: addDays(current.from, direction * days), from: addDays(current.from, direction * days), through: addDays(current.through, direction * days) }
    : { period: selection.period, date: addDays(current.date, direction * days) };
}

export function canMoveEnergySelection(selection: EnergySelection, today: string, direction: number): boolean {
  try { return energySelectionError(moveEnergySelection(selection, today, direction), today) === null; } catch { return false; }
}
export function energyCaption(selection: EnergySelection, today: string): string {
  const window = energyWindow(selection, today);
  return selection.period === "CalendarMonth" ? dateCaption(window.from, "Month")
    : window.from === window.through ? dateCaption(window.from) : `${dateCaption(window.from)} – ${dateCaption(window.through)}`;
}
export function energySelectionKey(selection: EnergySelection): string {
  return `${selection.period}:${selection.date ?? "today"}:${selection.from ?? ""}:${selection.through ?? ""}`;
}
export function productionPeriod(period: EnergyPeriod): SolarHistoryPeriod {
  return period === "Day" ? "Today" : period === "RollingMonth" ? "Month" : period;
}
export function salesPeriod(period: EnergyPeriod): ExportSalesPeriod {
  return period === "CalendarMonth" ? "Month" : period;
}
export function energyRequestRange(selection: EnergySelection, today: string) {
  if (selection.period !== "Custom") return undefined;
  const { from, through } = energyWindow(selection, today);
  return { from, through };
}
