type PowerReadings = {
  solarProduction?: number | null;
  gridConsumption?: number | null;
  batteryPower?: number | null;
  loadPower?: number | null;
  batteryPowerValid?: boolean | null;
  loadPowerValid?: boolean | null;
  gridPowerValid?: boolean | null;
  solarPowerValid?: boolean | null;
  solarObservedAt?: string | null;
  gridObservedAt?: string | null;
  solarDeviceSn?: string | null;
  gridDeviceSn?: string | null;
};

export function batteryFlow(power?: number | null, valid?: boolean | null) {
  if (valid === false || typeof power !== "number" || !Number.isFinite(power))
    return { label: "Battery power", watts: null };
  return {
    label: power < 0 ? "Battery charging" : power > 0 ? "Battery discharging" : "Battery idle",
    watts: Math.abs(power)
  };
}

// Grid import and battery discharge supply power; export and charging consume it.
export function calculatePowerBalance(readings?: PowerReadings | null): number | null {
  if (!readings || readings.batteryPowerValid === false || readings.loadPowerValid === false
    || readings.gridPowerValid === false || readings.solarPowerValid === false) return null;
  const { solarProduction: solar, gridConsumption: grid, batteryPower: battery, loadPower: load } = readings;
  if (![solar, grid, battery, load].every((value) => typeof value === "number" && Number.isFinite(value))
    || solar! < 0 || load! < 0) return null;
  const difference = solar! + grid! + battery! - load!;
  return Number.isFinite(difference) ? difference : null;
}

export function reportedPowerBalance(readings?: (PowerReadings & { timestamp: string }) | null,
  now = Date.now(), maximumAgeMs = 10 * 60 * 1000): { watts: number | null; reason: string | null } {
  const watts = calculatePowerBalance(readings);
  if (watts === null || !readings) return { watts: null, reason: "Required power readings are unavailable." };
  // Older deployments supply poll time only. New source metadata must be valid before using its readings.
  const timestamp = readings.timestamp;
  if (typeof timestamp !== "string") return { watts: null, reason: "A recent inverter poll is required to calculate the balance." };
  const polledAt = utcTime(timestamp);
  if (!Number.isFinite(polledAt) || !Number.isFinite(now) || !Number.isFinite(maximumAgeMs) || maximumAgeMs < 0
    || polledAt > now || now - polledAt > maximumAgeMs)
    return { watts: null, reason: "A recent inverter poll is required to calculate the balance." };
  if (hasSourceMetadata(readings)) {
    const solarAt = utcTime(readings.solarObservedAt);
    const gridAt = utcTime(readings.gridObservedAt);
    if (!Number.isFinite(solarAt) || !Number.isFinite(gridAt) || !readings.solarDeviceSn?.trim()
      || readings.solarDeviceSn !== readings.gridDeviceSn)
      return { watts: null, reason: "Valid solar and grid measurements from the same inverter are required." };
    if (solarAt > now || gridAt > now || solarAt > polledAt || gridAt > polledAt
      || now - solarAt > maximumAgeMs || now - gridAt > maximumAgeMs)
      return { watts: null, reason: "Recent solar and grid measurements are required to calculate the balance." };
    if (Math.abs(solarAt - gridAt) > 120_000)
      return { watts: null, reason: "Solar and grid measurement times are too far apart to calculate the balance." };
  }
  return { watts, reason: null };
}

export function hasSourceMetadata(readings: PowerReadings): boolean {
  return ["solarObservedAt", "gridObservedAt", "solarDeviceSn", "gridDeviceSn"].some((key) => key in readings);
}

function utcTime(value?: string | null): number {
  if (typeof value !== "string") return Number.NaN;
  return Date.parse(value.includes("T") && !/([zZ]|[+-]\d{2}:?\d{2})$/.test(value) ? `${value}Z` : value);
}

export function balanceDirection(watts: number): string {
  return watts > 0 ? "Reported supply exceeds consumption" : watts < 0 ? "Reported consumption exceeds supply" : "Reported supply and consumption match";
}

export function formatBalanceWatts(watts: number | null): string {
  if (watts === null || !Number.isFinite(watts)) return "—";
  return `${watts > 0 ? "+" : ""}${watts.toLocaleString("en-GB", { maximumFractionDigits: 2 })} W`;
}
