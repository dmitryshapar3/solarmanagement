export function formatWatts(value?: number | null): string {
  if (value === null || value === undefined) {
    return "-";
  }

  if (Math.abs(value) >= 1000) {
    return `${(value / 1000).toFixed(1)} kW`;
  }

  return `${value} W`;
}

export function formatSignedWatts(value?: number | null): string {
  if (value === null || value === undefined) {
    return "-";
  }

  const prefix = value > 0 ? "+" : value < 0 ? "-" : "";
  return `${prefix}${formatWatts(Math.abs(value))}`;
}

export function formatPercent(value?: number | null): string {
  return value === null || value === undefined ? "-" : `${value}%`;
}

let displayTimeZone: string | undefined;

export function setDisplayTimeZone(timeZoneId?: string | null) {
  displayTimeZone = timeZoneId || undefined;
}

// API DateTime fields (readings, rule-run logs, rule state timestamps) are UTC
// but serialize without an offset suffix; JS would parse them as local time.
function parseApiDate(value: string): Date {
  const hasOffset = /([zZ]|[+-]\d{2}:?\d{2})$/.test(value);
  const normalized = value.includes("T") && !hasOffset ? `${value}Z` : value;
  return new Date(normalized);
}

function formatInDisplayTimeZone(
  date: Date,
  format: (options: Intl.DateTimeFormatOptions) => string,
  options: Intl.DateTimeFormatOptions
): string {
  if (displayTimeZone) {
    try {
      return format({ ...options, timeZone: displayTimeZone });
    } catch {
      // unsupported timezone id on this device - fall back to device timezone
    }
  }

  return format(options);
}

export function formatDateTime(value?: string | null): string {
  if (!value) {
    return "-";
  }

  const date = parseApiDate(value);
  if (Number.isNaN(date.getTime())) {
    return "-";
  }

  return formatInDisplayTimeZone(date, (options) => date.toLocaleString(undefined, options), {
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit"
  });
}

export function formatTime(value?: string | null): string {
  if (!value) {
    return "-";
  }

  const date = parseApiDate(value);
  if (Number.isNaN(date.getTime())) {
    return "-";
  }

  return formatInDisplayTimeZone(date, (options) => date.toLocaleTimeString(undefined, options), {
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit"
  });
}

export function gridModeLabel(power: number): string {
  if (power < 0) {
    return "Exporting";
  }

  if (power > 0) {
    return "Importing";
  }

  return "Idle";
}
