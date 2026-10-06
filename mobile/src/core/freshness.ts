export function utcInstant(value?: string | null): number {
  if (!value) return NaN;
  return Date.parse(value.includes("T") && !/([zZ]|[+-]\d{2}:?\d{2})$/.test(value) ? `${value}Z` : value);
}
export function readingAge(value?: string | null, now = Date.now()): number | null {
  const at = utcInstant(value); return Number.isFinite(at) && at <= now ? Math.floor((now - at) / 1000) : null;
}
export function freshnessLabel(value: string | null | undefined, failed: boolean, t: (phrase: string, ...values: unknown[]) => string, now = Date.now()): string {
  const seconds = readingAge(value, now);
  if (seconds === null) return t("Waiting for a reading");
  if (failed) return t("No connection · showing {0}", new Date(utcInstant(value)).toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" }));
  if (seconds < 60) return t("Live · updated {0} s ago", seconds);
  return t("Updated {0} min ago · pull to refresh", Math.floor(seconds / 60));
}
