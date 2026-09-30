import assert from "node:assert/strict";
import test from "node:test";
import { addDays, amount, canSelectPreviousHistoryDay, chartGeometry, dateCaption, defaultPointIndex, momentCaption, movePeriod, periodAnchor, salesPointValues, zonedDate } from "../src/features/energy/chartPolicy";
import type { ChartPoint } from "../src/features/energy/chartPolicy";
import type { ExportSaleBucket, ExportSaleProgress } from "../src/core/api/types";

const start = "2026-09-29T22:00:00Z";
const point = (hour: number, values: Partial<ChartPoint> = {}): ChartPoint => ({
  timestamp: new Date(Date.parse(start) + hour * 3600000).toISOString(), label: `${hour}`, description: `Hour ${hour}`, ...values
});
const bucket = (changes: Partial<ExportSaleBucket> = {}): ExportSaleBucket => ({
  start, end: "2026-09-29T23:00:00Z", exportKwh: 2, creditedExportKwh: 1, energyValuePln: .5, estimatedDepositPln: .615,
  expectedHours: 1, observedHours: 1, valuedHours: 1, ...changes
});
const progress = (changes: Partial<ExportSaleProgress> = {}): ExportSaleProgress => ({
  start, observedThrough: "2026-09-29T22:15:00Z", exportKwh: .25, creditedExportKwh: .2, energyValuePln: .1, estimatedDepositPln: .123, observedSeconds: 900, ...changes
});

test("generation range polygons and actual lines never bridge missing hours", () => {
  const plot = chartGeometry([point(0, { possible: { lowerKw: 1, upperKw: 2 }, actual: 1.5 }), point(1), point(2, { possible: { lowerKw: 3, upperKw: 4 }, actual: 3.5 })], "generation");
  assert.equal(plot.bandPaths.length, 2);
  assert.equal(plot.actualPaths.length, 2);
  assert.equal(plot.max, 4);
  assert.ok(plot.bandPaths.every((path) => path.endsWith(" Z")));
  assert.ok(!plot.bandPaths.join().includes("NaN"));
});

test("omitted hourly records also split both generation series", () => {
  const plot = chartGeometry([point(0, { possible: { lowerKw: 1, upperKw: 2 }, actual: 1 }), point(3, { possible: { lowerKw: 2, upperKw: 3 }, actual: 2 })], "generation");
  assert.equal(plot.bandPaths.length, 2);
  assert.equal(plot.actualPaths.length, 2);
});

test("an isolated range retains its full hour width, including real night zero", () => {
  const plot = chartGeometry([point(0, { possible: { lowerKw: 0, upperKw: 0 }, actual: 0 })], "generation");
  assert.equal(plot.actualDots[0]?.y, plot.baseline);
  assert.match(plot.bandPaths[0]!, /^M38 176/);
  assert.ok(plot.bandPaths[0]!.includes("L348 176"));
  assert.equal(plot.max, 1);
  assert.equal(plot.bandPaths.length, 1);
});

test("invalid ranges and nonfinite values stay unavailable rather than corrupting geometry", () => {
  const plot = chartGeometry([point(0, { possible: { lowerKw: 4, upperKw: 2 }, actual: Number.NaN }), point(1, { actual: Number.POSITIVE_INFINITY })], "generation");
  assert.deepEqual(plot.bandPaths, []);
  assert.deepEqual(plot.actualPaths, []);
  assert.equal(amount(Number.NaN, "kW"), "— kW");
});

test("sales current-hour data is separate from completed and future zero placeholders", () => {
  const open = bucket({ expectedHours: 0, observedHours: 0, valuedHours: 0, exportKwh: 0, energyValuePln: 0 });
  const values = salesPointValues(open, progress(), "energy");
  assert.equal(values.completed, null);
  assert.equal(values.provisional, .25);
  assert.equal(open.exportKwh, 0);
  const plot = chartGeometry([point(0, values)], "sales");
  assert.equal(plot.bars.length, 1);
  assert.equal(plot.bars[0]?.provisional, true);
  assert.equal(plot.bars[0]?.height, 39.5);
  assert.equal(salesPointValues(bucket({ start: "2026-09-29T23:00:00Z", end: "2026-09-30T00:00:00Z", expectedHours: 0 }), progress(), "energy").provisional, null);
});

test("current-hour null prices remain missing while signed prices remain signed", () => {
  assert.equal(salesPointValues(bucket(), progress({ energyValuePln: null }), "value").provisional, null);
  assert.equal(salesPointValues(bucket(), progress({ energyValuePln: -.2 }), "value").provisional, -.2);
  const plot = chartGeometry([point(0, { completed: -.5, provisional: -.2 })], "sales");
  assert.equal(plot.min, -1);
  assert.equal(plot.bars.length, 2);
  assert.ok(plot.bars[1]!.y >= plot.baseline);
  assert.equal(amount(-.246, "PLN"), "-0.25 PLN");
});

test("calendar bucket scaling includes the separate current increment without changing totals", () => {
  const completed = bucket({ exportKwh: 2 });
  const values = salesPointValues(completed, progress({ exportKwh: 2 }), "energy");
  const plot = chartGeometry([point(0, values)], "sales");
  assert.equal(plot.max, 4);
  assert.equal(plot.bars[0]?.height, 79);
  assert.equal(plot.bars[1]?.height, 79);
  assert.equal(completed.exportKwh, 2);
});

test("measured sales zero has a visible baseline marker while unknown data is a gap", () => {
  const plot = chartGeometry([point(0, { completed: 0 }), point(1), point(2, { provisional: 0 })], "sales");
  assert.equal(plot.bars.length, 2);
  assert.ok(plot.bars.every((bar) => bar.y === plot.baseline && bar.height === 1.5));
  assert.equal(defaultPointIndex([point(0, { completed: 0 }), point(1)]), 0);
  assert.equal(amount(0, "PLN"), "0.00 PLN");
  assert.equal(amount(null, "PLN"), "— PLN");
});

test("Warsaw calendar navigation crosses DST and leap boundaries by calendar date", () => {
  assert.equal(zonedDate(new Date("2026-03-29T22:30:00Z")), "2026-03-30");
  assert.equal(zonedDate(new Date("2026-10-25T23:30:00Z")), "2026-10-26");
  assert.equal(addDays("2026-03-29", -1), "2026-03-28");
  assert.equal(addDays("2026-10-25", 1), "2026-10-26");
  assert.equal(addDays("2024-02-28", 1), "2024-02-29");
  assert.throws(() => addDays("2026-02-29", 1));
});

test("month and year arrows use calendar anchors and preserve future-bound comparisons", () => {
  assert.equal(movePeriod("2026-01-31", "Month", 1), "2026-02-01");
  assert.equal(movePeriod("2024-02-29", "Year", 1), "2025-01-01");
  assert.equal(movePeriod("2026-01-01", "Month", -1), "2025-12-01");
  assert.equal(periodAnchor("2026-09-30", "Month"), "2026-09-01");
  assert.ok(movePeriod("2026-09-30", "Month", 1) > periodAnchor("2026-09-30", "Month"));
  assert.equal(dateCaption("2026-09-30", "Month"), "September 2026");
});

test("generation history navigation stops at the inclusive 30-date server boundary", () => {
  assert.equal(canSelectPreviousHistoryDay("2026-09-30", "2026-09-30"), true);
  assert.equal(canSelectPreviousHistoryDay("2026-09-02", "2026-09-30"), true);
  assert.equal(canSelectPreviousHistoryDay("2026-09-01", "2026-09-30"), false);
  assert.equal(canSelectPreviousHistoryDay("2026-08-31", "2026-09-30"), false);
});

test("repeated autumn hours have distinct UTC offsets in the English inspector", () => {
  const first = momentCaption("2026-10-25T00:00:00Z", "Europe/Warsaw");
  const second = momentCaption("2026-10-25T01:00:00Z", "Europe/Warsaw");
  assert.match(first, /02:00.*GMT\+2/);
  assert.match(second, /02:00.*GMT\+1/);
  assert.notEqual(first, second);
  assert.equal(amount(1234.5, "kWh"), "1,234.50 kWh");
});
