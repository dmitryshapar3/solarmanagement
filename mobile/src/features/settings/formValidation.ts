export function parseSettingNumber(value: string): number {
  if (!value.trim()) return Number.NaN;
  return Number(value.trim().replace(",", "."));
}
export function settingNumberError(value: number, minimum: number, maximum: number, integer = false): string | null {
  if (!Number.isFinite(value) || integer && !Number.isInteger(value)) return integer ? "Enter a whole number." : "Enter a valid number.";
  return value < minimum || value > maximum ? "Use a value between {0} and {1}." : null;
}
export function settingBearingError(value: number, stored?: number): string | null {
  return value === 360 && stored === 360 ? null : settingNumberError(value, 0, 359);
}
export function settingTimeZoneError(value: string): string | null {
  if (!value.trim()) return "Choose a valid time zone.";
  try { new Intl.DateTimeFormat("en", { timeZone: value }); return null; }
  catch { return "Choose a valid time zone."; }
}
export function settingContractDateError(value: string): string | null {
  const parsed = /^\d{4}-\d{2}-\d{2}$/.test(value) ? Date.parse(`${value}T00:00:00Z`) : Number.NaN;
  return Number.isFinite(parsed) && new Date(parsed).toISOString().slice(0, 10) === value && value >= "2000-01-01" ? null : "Enter a contract start date from 2000 onwards as YYYY-MM-DD.";
}
