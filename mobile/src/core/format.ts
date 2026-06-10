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

export function formatDateTime(value?: string | null): string {
  if (!value) {
    return "-";
  }

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return "-";
  }

  return date.toLocaleString(undefined, {
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit"
  });
}

export function formatTime(value?: string | null): string {
  if (!value) {
    return "-";
  }

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return "-";
  }

  return date.toLocaleTimeString(undefined, {
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit"
  });
}

export function batteryModeLabel(power: number): string {
  if (power < 0) {
    return "Charging";
  }

  if (power > 0) {
    return "Discharging";
  }

  return "Idle";
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
