import type { InverterData } from "../../core/api/types";
import type { ProductionView } from "../../core/api/redesignTypes";
import { readingAge, utcInstant } from "../../core/freshness";

export function homeSunState(data: Pick<ProductionView, "date" | "today" | "sunrise" | "sunset" | "nextSunrise"> | null | undefined, now = Date.now()) {
  if (!data || data.date !== data.today || !Number.isFinite(now)) return { night: false, nextSunrise: null };
  const sunrise = utcInstant(data.sunrise), sunset = utcInstant(data.sunset), next = utcInstant(data.nextSunrise);
  if (Number.isFinite(sunrise) && now < sunrise) return { night: true, nextSunrise: data.sunrise };
  if (Number.isFinite(sunset) && now >= sunset && Number.isFinite(next) && next > now) return { night: true, nextSunrise: data.nextSunrise };
  return { night: false, nextSunrise: null };
}

export function homeUsesBattery(inverter: InverterData | null | undefined, now = Date.now()): boolean {
  if (!inverter) return false;
  const fresh = (at: string | null) => { const age = readingAge(at, now); return age !== null && age <= 600; };
  const polled = utcInstant(inverter.timestamp);
  const solarSource = inverter.solarDeviceSn?.trim(), gridSource = inverter.gridDeviceSn?.trim();
  return fresh(inverter.timestamp) && fresh(inverter.solarObservedAt) && fresh(inverter.gridObservedAt)
    && utcInstant(inverter.solarObservedAt) <= polled && utcInstant(inverter.gridObservedAt) <= polled
    && Boolean(inverter.inverterId?.trim() && inverter.dataSource?.trim() && solarSource && gridSource)
    && solarSource!.toLowerCase() === gridSource!.toLowerCase()
    && inverter.solarPowerValid && Number.isFinite(inverter.solarProduction) && inverter.solarProduction >= 0 && inverter.solarProduction < 50
    && inverter.batteryPowerValid && Number.isFinite(inverter.batteryPower) && inverter.batteryPower > 0
    && inverter.gridPowerValid && Number.isFinite(inverter.gridConsumption) && inverter.gridConsumption <= 0
    && inverter.loadPowerValid && Number.isFinite(inverter.loadPower) && inverter.loadPower > 0;
}
