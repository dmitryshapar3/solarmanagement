import { formattingLocale, translate as t } from "../../core/i18n";
import type { ExportSaleBucket, ExportSaleProgress } from "../../core/api/types";

export type PowerRange = { lowerKw: number; upperKw: number };
export type ChartPoint = {
  timestamp: string;
  label: string;
  description: string;
  possible?: PowerRange | null;
  actual?: number | null;
  completed?: number | null;
  provisional?: number | null;
};

export const chartWidth = 360;
export const chartHeight = 210;
const left = 38;
const right = 12;
const top = 18;
const bottom = 176;

export function known(value: number | null | undefined): value is number {
  return value !== null && value !== undefined && Number.isFinite(value);
}

export function amount(value: number | null | undefined, unit: string, digits = 2): string {
  return `${known(value) ? value.toLocaleString(formattingLocale(), { minimumFractionDigits: digits, maximumFractionDigits: digits }) : "—"} ${unit}`;
}

export function zonedDate(now: Date, timeZone = "Europe/Warsaw"): string {
  const parts = new Intl.DateTimeFormat("en-GB", { timeZone, year: "numeric", month: "2-digit", day: "2-digit" }).formatToParts(now);
  return ["year", "month", "day"].map((name) => parts.find((part) => part.type === name)?.value).join("-");
}

function calendarDate(value: string): Date {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) throw new Error(t("Invalid calendar date."));
  const date = new Date(`${value}T12:00:00Z`);
  if (!Number.isFinite(date.getTime()) || date.toISOString().slice(0, 10) !== value) throw new Error(t("Invalid calendar date."));
  return date;
}

export function addDays(value: string, days: number): string {
  const date = calendarDate(value);
  date.setUTCDate(date.getUTCDate() + days);
  return date.toISOString().slice(0, 10);
}

export function canSelectPreviousHistoryDay(selected: string, today: string): boolean {
  return selected > addDays(today, -29);
}

export function periodAnchor(date: string, period: "Day" | "Month" | "Year"): string {
  calendarDate(date);
  return period === "Month" ? `${date.slice(0, 7)}-01` : period === "Year" ? `${date.slice(0, 4)}-01-01` : date;
}

export function movePeriod(value: string, period: "Day" | "Month" | "Year", direction: number): string {
  const date = calendarDate(periodAnchor(value, period));
  if (period === "Year") date.setUTCFullYear(date.getUTCFullYear() + direction);
  else if (period === "Month") date.setUTCMonth(date.getUTCMonth() + direction);
  else date.setUTCDate(date.getUTCDate() + direction);
  return date.toISOString().slice(0, 10);
}

export function dateCaption(date: string, period: "Day" | "Month" | "Year" = "Day"): string {
  return calendarDate(date).toLocaleDateString(formattingLocale(), {
    timeZone: "UTC", year: "numeric", month: period === "Year" ? undefined : "long", day: period === "Day" ? "numeric" : undefined
  });
}

export function momentCaption(value: string, timeZone: string): string {
  const date = new Date(value);
  if (!Number.isFinite(date.getTime())) return t("Time unavailable");
  try {
    return date.toLocaleString(formattingLocale(), { timeZone, day: "numeric", month: "short", hour: "2-digit", minute: "2-digit", timeZoneName: "shortOffset" });
  } catch {
    return date.toISOString();
  }
}

export function tickCaption(value: string, timeZone: string, period: "Day" | "Month" | "Year" = "Day"): string {
  return new Date(value).toLocaleString(formattingLocale(), { timeZone,
    hour: period === "Day" ? "2-digit" : undefined, hour12: false,
    day: period === "Month" ? "numeric" : undefined, month: period === "Year" ? "short" : undefined });
}

export function validRange(range: PowerRange | null | undefined): range is PowerRange {
  return Boolean(range && known(range.lowerKw) && known(range.upperKw) && range.lowerKw >= 0 && range.upperKw >= range.lowerKw);
}

export function chartGeometry(points: readonly ChartPoint[], mode: "generation" | "sales", domain?: { minimum: number; maximum: number }) {
  const values = points.flatMap((point) => mode === "generation"
    ? [validRange(point.possible) ? point.possible.upperKw : null, point.actual]
    : [point.completed, known(point.provisional) ? (point.completed ?? 0) + point.provisional : null]).filter(known);
  const bounded = domain && known(domain.minimum) && known(domain.maximum) && domain.maximum > domain.minimum;
  const min = bounded ? domain.minimum : Math.min(0, Math.floor(Math.min(0, ...values)));
  const max = bounded ? domain.maximum : Math.max(1, Math.ceil(Math.max(0, ...values)));
  const step = (chartWidth - left - right) / Math.max(1, points.length);
  const x = (index: number) => left + step * (index + .5);
  const y = (value: number) => bottom - (value - min) / (max - min) * (bottom - top);
  const coordinate = (value: number) => Number(value.toFixed(3));
  const bandPaths: string[] = [];
  const actualPaths: string[] = [];
  const actualDots: { x: number; y: number }[] = [];
  let run: number[] = [];
  const finishBand = () => {
    if (!run.length) return;
    const first = run[0]!;
    const last = run[run.length - 1]!;
    const vertices = [
      [x(first) - step / 2, y(points[first]!.possible!.upperKw)],
      ...run.map((index) => [x(index), y(points[index]!.possible!.upperKw)]),
      [x(last) + step / 2, y(points[last]!.possible!.upperKw)],
      [x(last) + step / 2, y(points[last]!.possible!.lowerKw)],
      ...[...run].reverse().map((index) => [x(index), y(points[index]!.possible!.lowerKw)]),
      [x(first) - step / 2, y(points[first]!.possible!.lowerKw)]
    ];
    bandPaths.push(vertices.map((point, index) => `${index ? "L" : "M"}${coordinate(point[0]!)} ${coordinate(point[1]!)}`).join(" ") + " Z");
    run = [];
  };
  let actual = "";
  points.forEach((point, index) => {
    const previous = points[index - 1];
    const gap = previous && new Date(point.timestamp).getTime() - new Date(previous.timestamp).getTime() > 3600000;
    if (gap || !validRange(point.possible)) finishBand();
    if (validRange(point.possible)) run.push(index);
    if (gap || !known(point.actual)) {
      if (actual) actualPaths.push(actual);
      actual = "";
    }
    if (known(point.actual)) {
      actual += `${actual ? " L" : "M"}${coordinate(x(index))} ${coordinate(y(point.actual))}`;
      actualDots.push({ x: x(index), y: y(point.actual) });
    }
  });
  finishBand();
  if (actual) actualPaths.push(actual);
  const bars = points.flatMap((point, index) => {
    const bar = (start: number, end: number, provisional: boolean) => ({
      x: x(index) - step * .32, y: Math.min(y(start), y(end)), width: Math.max(.5, step * .64),
      height: Math.max(1.5, Math.abs(y(end) - y(start))), provisional, index
    });
    return [
      ...(known(point.completed) ? [bar(0, point.completed, false)] : []),
      ...(known(point.provisional) ? [bar(point.completed ?? 0, (point.completed ?? 0) + point.provisional, true)] : [])
    ];
  });
  return { min, max, step, x, y, baseline: y(0), bandPaths, actualPaths, actualDots, bars,
    ticks: Array.from({ length: 5 }, (_, index) => ({ value: min + (max - min) * index / 4, y: y(min + (max - min) * index / 4) })) };
}

export function defaultPointIndex(points: readonly ChartPoint[]): number {
  for (let index = points.length - 1; index >= 0; index--)
    if (validRange(points[index]?.possible) || known(points[index]?.actual) || known(points[index]?.completed) || known(points[index]?.provisional)) return index;
  return 0;
}
export function salesPointValues(bucket: ExportSaleBucket, progress: ExportSaleProgress | null, metric: "energy" | "value") {
  const current = progress && Date.parse(progress.start) >= Date.parse(bucket.start) && Date.parse(progress.start) < Date.parse(bucket.end) ? progress : null;
  return {
    current,
    completed: bucket.expectedHours > 0 ? metric === "energy" ? bucket.exportKwh : bucket.energyValuePln : null,
    provisional: current ? metric === "energy" ? current.exportKwh : current.energyValuePln : null
  };
}
