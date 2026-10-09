import assert from "node:assert/strict";
import test from "node:test";
import React from "react";
import { act, create } from "react-test-renderer";
import { ApiClient, ApiError } from "../src/core/api/ApiClient";
import { DeyeSolarApi } from "../src/core/api/DeyeSolarApi";
import { validApiResponse } from "../src/core/api/responseContracts";
import { DemoApiClient } from "../src/features/demo/DemoApiClient";
import { demoProduction, demoSales } from "../src/features/demo/fixtures";
import { chartGeometry, defaultPointIndex, type ChartPoint } from "../src/features/energy/chartPolicy";
import { measuredSalesRecheckRange } from "../src/features/sales/exportPolicy";
import { globals, uiHarness } from "./support/uiHarness";

const now = new Date("2026-10-09T11:25:00Z");
const range = { from: "2026-10-09", through: "2026-11-07" };

test("production and sales send their explicit calendar, rolling and custom periods without changing legacy URLs", async () => {
  const requests: URL[] = [];
  const api = new DeyeSolarApi(new ApiClient({ baseUrl: "https://solar.example", transport: async url => {
    const request = new URL(url); requests.push(request);
    const body = request.pathname === "/api/solar/production" ? demoProduction("Today", undefined, now, "UTC") : demoSales("Day", "2026-10-09", now, "UTC");
    return new Response(JSON.stringify(body));
  } }));
  await api.getProduction("Today");
  assert.equal(requests.at(-1)!.search, "?period=Today");
  await api.getProduction("CalendarMonth", "2024-02-12");
  assert.equal(requests.at(-1)!.searchParams.get("period"), "CalendarMonth");
  await api.getProduction("Custom", "2026-10-09", undefined, range);
  assert.deepEqual(Object.fromEntries(requests.at(-1)!.searchParams), { period: "Custom", date: "2026-10-09", ...range });
  await api.getSales("Month", "2026-10-01");
  assert.equal(requests.at(-1)!.search, "?period=Month&date=2026-10-01");
  for (const period of ["Week", "RollingMonth", "Custom"] as const) {
    await api.getSalesDetails(period, "2026-11-07", undefined, period === "Custom" ? range : undefined, { includeUpcoming: true });
    assert.equal(requests.at(-1)!.searchParams.get("period"), period);
    assert.equal(requests.at(-1)!.searchParams.get("details"), "true");
    assert.equal(requests.at(-1)!.searchParams.get("includeUpcoming"), "true");
  }
  await api.getSalesDetails("Day", "2026-10-09", undefined, undefined, { includeUpcoming: false });
  assert.equal(requests.at(-1)!.searchParams.has("includeUpcoming"), false);
});

test("additive forecast metadata validates while older production payloads still work", () => {
  const data = demoProduction("Month", "2026-11-07", now, "UTC");
  assert.equal(validApiResponse("/api/solar/production", "GET", data), true);
  const { firstDate, lastDate, forecastAvailableFrom, forecastAvailableThrough, forecastIncomplete, availableExpectedEnergyKwh, bestForecastHour, ...old } = data;
  assert.equal(validApiResponse("/api/solar/production", "GET", old), true);
  for (const invalid of [{ forecastIncomplete: "true" }, { availableExpectedEnergyKwh: Number.NaN }, { forecastAvailableThrough: 16 }, { bestForecastHour: { ...bestForecastHour, expectedKw: "5" } }]) {
    assert.equal(validApiResponse("/api/solar/production", "GET", { ...data, ...invalid }), false);
  }
  for (const period of ["Week", "RollingMonth"] as const) assert.equal(validApiResponse("/api/sales", "GET", demoSales(period, "2026-10-09", now, "UTC")), true);
});

test("price rechecks use only the measured intersection and preserve the existing POST contract", async () => {
  assert.deepEqual(measuredSalesRecheckRange("2026-10-01", "2026-11-01", "2026-10-09"), { from: "2026-10-01", through: "2026-10-09" });
  assert.deepEqual(measuredSalesRecheckRange("2026-10-01", "2026-10-08", "2026-10-09"), { from: "2026-10-01", through: "2026-10-08" });
  assert.deepEqual(measuredSalesRecheckRange("2026-10-09", "2026-10-09", "2026-10-09"), { from: "2026-10-09", through: "2026-10-09" });
  for (const [from, through] of [["2026-10-10", "2026-10-15"], ["2026-10-10", "2026-10-01"], ["2026-02-29", "2026-10-09"], ["2026-10-01", "invalid"], ["1999-12-31", "2000-01-01"]]) {
    assert.equal(measuredSalesRecheckRange(from!, through!, "2026-10-09"), null);
  }
  let request: { url: string; body?: string; method: string } | undefined;
  const api = new DeyeSolarApi(new ApiClient({ baseUrl: "https://solar.example", transport: async (url, init) => {
    request = { url, body: init.body, method: init.method };
    return new Response(JSON.stringify(demoSales("Day", "2026-10-09", now, "UTC")));
  } }));
  const measured = measuredSalesRecheckRange("2026-10-01", "2026-11-01", "2026-10-09")!;
  await api.recheckSalesPrices("Custom", measured.from, measured);
  assert.equal(request!.url, "https://solar.example/api/sales/prices/recheck");
  assert.equal(request!.method, "POST");
  assert.deepEqual(JSON.parse(request!.body!), { period: "Custom", date: "2026-10-01", from: "2026-10-01", through: "2026-10-09" });
});

test("demo forward month reports only sixteen forecast dates and never invents future measurements", async () => {
  const api = new DeyeSolarApi(new DemoApiClient(() => now));
  const month = await api.getProduction("Month", "2026-11-07");
  assert.equal(month.days.length, 30);
  assert.equal(month.firstDate, "2026-10-09");
  assert.equal(month.lastDate, "2026-11-07");
  assert.equal(month.forecastAvailableThrough, "2026-10-24");
  assert.equal(month.forecastIncomplete, true);
  assert.equal(month.expectedEnergyKwh, null);
  assert.ok(month.availableExpectedEnergyKwh! > 0);
  assert.equal(month.days.filter(day => day.expectedEnergyKwh !== null).length, 16);
  assert.equal(month.days.filter(day => day.date > "2026-10-24").every(day => day.expectedEnergyKwh === null), true);
  const future = await api.getProduction("Today", "2026-10-10");
  assert.equal(future.hours.every(hour => hour.actualKw === null && hour.observedEnergyKwh === null && hour.coveredSeconds === 0), true);
  assert.equal(future.observedEnergyKwh, null);
  assert.equal(future.currentHour, null);
  assert.equal(future.bestHour, null);
  assert.ok(future.bestForecastHour!.expectedKw! > 0);
  const unavailable = await api.getProduction("Today", "2026-10-25");
  assert.equal(unavailable.hours.every(hour => hour.expectedKw === null), true);
  assert.equal(unavailable.expectedEnergyKwh, null);
  assert.equal(unavailable.availableExpectedEnergyKwh, null);
  assert.equal(unavailable.forecastAvailableFrom, null);
  assert.equal(unavailable.bestForecastHour, null);
});

test("demo calendar month, inclusive custom bounds and DST agree with server calendar windows", async () => {
  const api = new DeyeSolarApi(new DemoApiClient(() => now));
  const february = await api.getProduction("CalendarMonth", "2024-02-12");
  assert.equal(february.firstDate, "2024-02-01");
  assert.equal(february.lastDate, "2024-02-29");
  assert.equal(february.days.length, 29);
  const leapYear = await api.getProduction("Custom", "2024-01-01", undefined, { from: "2024-01-01", through: "2024-12-31" });
  assert.equal(leapYear.days.length, 366);
  for (const invalid of [{ from: "2024-01-01", through: "2025-01-01" }, { from: "2026-10-10", through: "2026-10-09" }, { from: "1999-12-31", through: "2000-01-01" }]) {
    await assert.rejects(api.getProduction("Custom", "2026-10-09", undefined, invalid), error => error instanceof ApiError && error.status === 400);
  }
  await assert.rejects(api.getProduction("Custom", "2026-10-09"), error => error instanceof ApiError && error.status === 400);
  for (const [date, hours] of [["2026-03-29", 23], ["2026-10-25", 25]] as const) {
    const data = await api.getProduction("Today", date);
    assert.equal(data.hours.length, hours);
    assert.equal((Date.parse(data.end) - Date.parse(data.start)) / 3600000, hours);
  }
});

test("demo future sales is explicit, keeps unobserved buckets empty and retains rolling range lengths", async () => {
  const api = new DeyeSolarApi(new DemoApiClient(() => now));
  await assert.rejects(api.getSalesDetails("Week", "2026-10-15"), error => error instanceof ApiError && error.status === 400);
  for (const [period, date, days] of [["Week", "2026-10-15", 7], ["RollingMonth", "2026-11-07", 30]] as const) {
    const data = await api.getSalesDetails(period, date, undefined, undefined, { includeUpcoming: true });
    assert.equal(data.buckets.length, days);
    assert.equal(data.request.date, date);
    assert.equal(data.hours!.every(hour => Date.parse(hour.start) < now.getTime()), true);
    const future = data.buckets.filter(bucket => Date.parse(bucket.start) > now.getTime());
    assert.ok(future.length > 0);
    assert.equal(future.every(bucket => bucket.exportKwh === null && bucket.energyValuePln === null && bucket.observedHours === 0), true);
  }
});

const point = (hour: number, expected?: number | null, actual?: number | null): ChartPoint => ({
  timestamp: new Date(Date.parse("2026-10-10T00:00:00Z") + hour * 3600000).toISOString(), label: `${hour}`, description: `${hour}`, expected, actual
});
test("central forecast geometry scales without measurements and splits unavailable or omitted intervals", () => {
  const plot = chartGeometry([point(0, 0), point(1, 5), point(2, null), point(3, 2), point(6, 3)], "generation");
  assert.equal(plot.max, 5);
  assert.deepEqual(plot.actualPaths, []);
  assert.equal(plot.expectedPaths.length, 3);
  assert.equal(plot.expectedDots.length, 4);
  assert.equal(plot.expectedDots[0]!.y, plot.baseline);
  assert.equal(plot.expectedDots[1]!.y, 18);
  assert.equal(defaultPointIndex([point(0), point(1, 0), point(2)]), 1);
  assert.equal(chartGeometry([point(0, Number.NaN)], "generation").expectedPaths.length, 0);
});

test("daily forecast joins adjacent dates independently from missing measurements and clips unavailable tails", () => {
  const plot = chartGeometry([point(0, 10), point(24, 15), point(48, null), point(72, 12)], "generation", undefined, 86400000);
  assert.equal(plot.expectedPaths.length, 2);
  assert.match(plot.expectedPaths[0]!, / L/);
  assert.equal(plot.actualPaths.length, 0);
  assert.equal(plot.expectedDots.length, 3);
});

test("hourly and daily chart rendering visibly separates central forecasts from actual values", async () => {
  const { ProductionChart } = await uiHarness('export { ProductionChart } from "./src/ui/charts/ProductionChart";');
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  let renderer: ReturnType<typeof create> | undefined;
  try {
    for (const bars of [false, true]) {
      const step = bars ? 24 : 1;
      const points = [point(0, 0), point(step, 5), point(2 * step, null), point(3 * step, 2)];
      await act(async () => { renderer = create(React.createElement(ProductionChart!, { points, bars })); });
      const series = renderer!.root.findAllByProps({ testID: "forecast-series" });
      assert.equal(series.length, 2);
      assert.ok(series.every(node => node.props.strokeDasharray === "6 4" && /^M/.test(node.props.d) && !node.props.d.includes("NaN")));
      const dots = renderer!.root.findAllByProps({ testID: "forecast-point" });
      assert.equal(dots.length, 3);
      assert.equal(dots[0]!.props.cy, 176);
      assert.equal(dots[1]!.props.cy, 18);
      await act(async () => { renderer!.unmount(); }); renderer = undefined;
    }
  } finally {
    if (renderer) await act(async () => renderer!.unmount());
    delete globals.IS_REACT_ACT_ENVIRONMENT;
  }
});
