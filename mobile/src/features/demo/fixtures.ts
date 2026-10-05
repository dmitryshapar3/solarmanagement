import type {
  Device, ExportSaleBucket, ExportSalesPeriod, ExportSalesResult,
  InverterData, Reading, Rule, RuleRunLog, Settings, SolarEstimateState, SolarHistoryPeriod, SolarHistoryResult, SolarSiteSettings
} from "../../core/api/types";
import { addDays, movePeriod, periodAnchor, zonedDate } from "../energy/chartPolicy";

export const DEMO_API_BASE_URL = "https://demo.invalid";
export const DEMO_USERNAME = "Demo";

export type DemoState = {
  devices: Device[];
  deviceRatedPowerW: Record<string, number>;
  rules: Rule[];
  settings: Settings;
  site: SolarSiteSettings;
  runs: RuleRunLog[];
};

export function createDemoState(now: Date): DemoState {
  const timestamp = now.toISOString();
  const devices: Device[] = [
    { id: "b5dce685-8c30-4d48-b26b-4d267b8bf5a1", name: "Demo water heater", category: "Socket", online: true, isOn: true, stateKnown: true, currentPowerW: 850 },
    { id: "9ac23f08-1bb3-44b4-8d4b-4f52136859ae", name: "Demo garden lights", category: "Socket", online: true, isOn: false, stateKnown: true, currentPowerW: 0 }
  ];
  const rules: Rule[] = [
    {
      id: 1, configurationVersion: "1".padStart(64, "0"), sourceInverterId: null, name: "Demo solar surplus", entityId: devices[0]!.id, enabled: true,
      socTurnOnThreshold: 75, useSeparateSocTurnOffThreshold: true, socTurnOffThreshold: 55,
      useSolarProductionThreshold: true, minAverageSolarProductionWatts: 1800,
      cooldownMinutes: 10, intervalSeconds: 60, activeFrom: "08:00", activeTo: "18:00",
      currentState: true, currentStateChangedAt: new Date(now.getTime() - 40 * 60000).toISOString(), lastEvaluated: timestamp
    },
    {
      id: 2, configurationVersion: "2".padStart(64, "0"), sourceInverterId: null, name: "Demo battery reserve", entityId: devices[1]!.id, enabled: false,
      socTurnOnThreshold: 85, useSeparateSocTurnOffThreshold: true, socTurnOffThreshold: 65,
      useSolarProductionThreshold: false, minAverageSolarProductionWatts: 2000,
      cooldownMinutes: 15, intervalSeconds: 60, activeFrom: null, activeTo: null,
      currentState: false, currentStateChangedAt: new Date(now.getTime() - 90 * 60000).toISOString(), lastEvaluated: timestamp
    }
  ];
  return {
    devices, rules, deviceRatedPowerW: { [devices[0]!.id]: 850, [devices[1]!.id]: 45 },
    settings: {
      polling: { intervalSeconds: 30 }, display: { timeZoneId: "Europe/Warsaw" }
    },
    site: {
      selectedDeviceSn: "DEMO-INVERTER-001",
      solarEstimate: { latitude: 50, longitude: 20, locationLabel: "Demo rooftop", timeZoneId: "Europe/Warsaw",
        roof1Kwp: 3.5, roof2Kwp: 3, roof1Tilt: 25, roof2Tilt: 25, roof1Azimuth: 180, roof2Azimuth: 90, deyeSolarPowerIsPvDcConfirmed: true, deyeSolarPowerConfirmedDeviceSn: "DEMO-INVERTER-001" },
      solarSales: { contractStartDate: "2026-01-01", timeZoneId: "Europe/Warsaw", payNegativePrices: false }
    },
    runs: Array.from({ length: 16 }, (_, index): RuleRunLog => ({
      id: index + 1, timestamp: new Date(now.getTime() - (index * 25 + 5) * 60000).toISOString(),
      ruleName: rules[index % 2]!.name, action: index % 3 === 0 ? "NO_CHANGE" : index % 2 === 0 ? "ON" : "OFF",
      conditionKey: "demo", reason: "Synthetic demonstration history; no hardware was contacted.",
      batterySoc: 76 + index % 8, solarProduction: 2400 + index % 5 * 180, batteryPower: -420
    }))
  };
}

const hourFormatters = new Map<string, Intl.DateTimeFormat>();
function localHour(date: Date, timeZone: string): number {
  let formatter = hourFormatters.get(timeZone);
  if (!formatter) {
    formatter = new Intl.DateTimeFormat("en-GB", { timeZone, hour: "2-digit", hourCycle: "h23" });
    hourFormatters.set(timeZone, formatter);
  }
  return Number(formatter.format(date));
}

// These curves are illustrative fixtures, not weather or billing calculations.
function solarKw(date: Date, timeZone: string): number {
  const hour = localHour(date, timeZone);
  return Number((Math.max(0, Math.sin(Math.PI * (hour - 6) / 12)) * (hour >= 6 && hour <= 18 ? 5.2 : 0)).toFixed(2));
}

function midnight(date: string, timeZone: string): Date {
  const target = Date.parse(`${date}T00:00:00Z`);
  if (!Number.isFinite(target) || new Date(target).toISOString().slice(0, 10) !== date) throw new Error("Invalid demo date.");
  let instant = target;
  const formatter = new Intl.DateTimeFormat("en-GB", {
    timeZone, year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit", hourCycle: "h23"
  });
  for (let attempt = 0; attempt < 3; attempt++) {
    const parts = formatter.formatToParts(new Date(instant));
    const get = (name: string) => parts.find(part => part.type === name)?.value ?? "00";
    const represented = Date.parse(`${get("year")}-${get("month")}-${get("day")}T${get("hour")}:${get("minute")}:${get("second")}Z`);
    instant += target - represented;
  }
  return new Date(instant);
}

export function demoInverter(now: Date, timeZone: string): InverterData {
  const solarProduction = Math.round(solarKw(now, timeZone) * 900);
  return {
    inverterId: "766ce5fb-f18b-4438-8718-d837397a7c78", solarObservedAt: now.toISOString(), gridObservedAt: now.toISOString(),
    solarDeviceSn: "DEMO-INVERTER-001", gridDeviceSn: "DEMO-INVERTER-001",
    batterySocValid: true, batteryPowerValid: true, batteryTemperatureValid: true, batteryVoltageValid: true,
    batteryCurrentValid: true, loadPowerValid: true, gridPowerValid: true, solarPowerValid: true,
    batterySoc: 78, batteryTemperature: 24.5, batteryVoltage: 51.8,
    batteryPower: solarProduction > 1000 ? -420 : 420, batteryCurrent: 8.1,
    solarProduction, gridConsumption: solarProduction > 1000 ? -Math.round(solarProduction * .4) : 180,
    loadPower: 1240, timestamp: now.toISOString(), dataSource: "Demo · synthetic data"
  };
}

export function demoEstimate(now: Date, timeZone: string): SolarEstimateState {
  const power = solarKw(now, timeZone);
  const estimate = {
    timestamp: now.toISOString(), calculatedAt: now.toISOString(), basis: 0 as const,
    centralKw: power, lowerKw: Number((power * .8).toFixed(2)), upperKw: Number((power * 1.2).toFixed(2)),
    totalKwp: 6.5, weatherMissing: false,
    observation: { timestamp: now.toISOString(), kind: 1 as const, retrievedAt: now.toISOString(), weatherTimestamp: now.toISOString() }
  };
  return {
    estimate, comparisonEstimate: estimate,
    comparison: { status: 1, actual: { timestamp: now.toISOString(), powerKw: power * .9, basis: 0 }, deviationKw: -power * .1, deviationPercent: power ? -10 : 0, reason: null },
    refreshFailed: false, lastSuccessAt: now.toISOString(), error: null
  };
}

export function demoSolarHistory(period: SolarHistoryPeriod, date: string | undefined, now: Date, timeZone: string): SolarHistoryResult {
  const today = zonedDate(now, timeZone);
  const selectedDate = date ?? today;
  const days = period === "Week" ? 7 : period === "Month" ? 30 : 1;
  const start = midnight(addDays(selectedDate, 1 - days), timeZone);
  const end = midnight(addDays(selectedDate, 1), timeZone);
  const points = [];
  for (let instant = start.getTime(); instant < end.getTime(); instant += 3600000) {
    const timestamp = new Date(instant);
    const power = solarKw(timestamp, timeZone);
    points.push({
      timestamp: timestamp.toISOString(), possible: { lowerKw: Number((power * .8).toFixed(2)), upperKw: Number((power * 1.2).toFixed(2)) },
      actualKw: instant <= now.getTime() ? Number((power * (.88 + .04 * Math.sin(instant / 3600000))).toFixed(2)) : null
    });
  }
  return { start: start.toISOString(), end: end.toISOString(), timeZoneId: timeZone, points, weatherError: null, actualError: null, selectedDate, today };
}

export function demoReadings(hours: number, now: Date, timeZone: string): Reading[] {
  return Array.from({ length: Math.min(500, Math.max(1, Math.ceil(hours * 4))) }, (_, index) => {
    const date = new Date(now.getTime() - index * 15 * 60000);
    return { ...demoInverter(date, timeZone), id: index + 1, batterySoc: 74 + index % 10 };
  });
}

export function demoSales(period: ExportSalesPeriod, date: string, now: Date, timeZone: string): ExportSalesResult {
  const calendarPeriod = period === "Custom" ? "Day" : period;
  const anchor = periodAnchor(date, calendarPeriod);
  const start = midnight(anchor, timeZone);
  const end = midnight(movePeriod(anchor, calendarPeriod, 1), timeZone);
  const currentStart = Math.floor(now.getTime() / 3600000) * 3600000;
  const bucket = (from: Date, through: Date): ExportSaleBucket => {
    let exportKwh = 0;
    let expectedHours = 0;
    for (let instant = from.getTime(); instant + 3600000 <= Math.min(through.getTime(), currentStart); instant += 3600000) {
      exportKwh += solarKw(new Date(instant), timeZone) * .4;
      expectedHours++;
    }
    return {
      start: from.toISOString(), end: through.toISOString(), exportKwh: expectedHours ? rounded(exportKwh) : null,
      creditedExportKwh: expectedHours ? rounded(exportKwh * .95) : null,
      energyValuePln: expectedHours ? rounded(exportKwh * .52) : null,
      estimatedDepositPln: expectedHours ? rounded(exportKwh * .52 * 1.23) : null,
      expectedHours, observedHours: expectedHours, valuedHours: expectedHours
    };
  };
  const buckets: ExportSaleBucket[] = [];
  if (calendarPeriod === "Day") {
    for (let instant = start.getTime(); instant < end.getTime(); instant += 3600000) buckets.push(bucket(new Date(instant), new Date(instant + 3600000)));
  } else {
    for (let cursor = anchor; cursor < movePeriod(anchor, calendarPeriod, 1);) {
      const next = calendarPeriod === "Year" ? movePeriod(cursor, "Month", 1) : addDays(cursor, 1);
      buckets.push(bucket(midnight(cursor, timeZone), midnight(next, timeZone)));
      cursor = next;
    }
  }
  const totals = bucket(start, end);
  const hasCurrent = now.getTime() >= start.getTime() && now.getTime() < end.getTime();
  const seconds = Math.floor((now.getTime() - currentStart) / 1000);
  const currentExport = solarKw(new Date(currentStart), timeZone) * .4 * seconds / 3600;
  return {
    request: { period: period === "Day" ? 0 : period === "Month" ? 1 : period === "Year" ? 2 : 3, date: anchor, from: period === "Custom" ? date : null, through: period === "Custom" ? date : null },
    today: zonedDate(now, timeZone), contractStartDate: "2000-01-01", timeZoneId: timeZone, ...totals, buckets,
    dataError: null, priceError: null, updatedAt: now.toISOString(), isPartial: false,
    currentHour: hasCurrent ? {
      start: new Date(currentStart).toISOString(), observedThrough: now.toISOString(), exportKwh: rounded(currentExport),
      creditedExportKwh: rounded(currentExport * .95), energyValuePln: rounded(currentExport * .52),
      estimatedDepositPln: rounded(currentExport * .52 * 1.23), observedSeconds: seconds
    } : null
  };
}

function rounded(value: number): number { return Number(value.toFixed(2)); }
