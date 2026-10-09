import assert from "node:assert/strict";
import { test } from "node:test";
import React from "react";
import { act, create, type ReactTestInstance } from "react-test-renderer";
import { readFileSync } from "node:fs";
import { addDays } from "../src/features/energy/chartPolicy";
import { canMoveEnergySelection, chooseEnergyPeriod, energyRequestRange, energySelectionError, energyWindow, moveEnergySelection, productionPeriod, salesPeriod, type EnergySelection } from "../src/features/energy/energySelection";
import { createDemoState, demoProduction, demoSales } from "../src/features/demo/fixtures";
import { globals, uiHarness } from "./support/uiHarness";

const today = "2026-10-08";
const now = new Date(`${today}T10:20:00Z`);
test("shared energy filters map the same inclusive window to both native APIs", () => {
  for (const [period, production, sales, from, through] of [
    ["Day", "Today", "Day", "2024-02-29", "2024-02-29"],
    ["Week", "Week", "Week", "2024-02-23", "2024-02-29"],
    ["RollingMonth", "Month", "RollingMonth", "2024-01-31", "2024-02-29"],
    ["CalendarMonth", "CalendarMonth", "Month", "2024-02-01", "2024-02-29"],
    ["Custom", "Custom", "Custom", "2024-02-04", "2024-02-12"]
  ] as const) {
    const selection: EnergySelection = { period, date: "2024-02-29", ...(period === "Custom" ? { from, through } : {}) };
    assert.equal(productionPeriod(period), production); assert.equal(salesPeriod(period), sales);
    assert.deepEqual(energyWindow(selection, today), { date: "2024-02-29", from, through });
    assert.equal(energySelectionError(selection, today), null);
    assert.deepEqual(energyRequestRange(selection, today), period === "Custom" ? { from, through } : undefined);
  }
  assert.deepEqual(energyWindow({ period: "Day" }, today), { date: today, from: today, through: today });
});
test("calendar validation rejects invalid, reversed, overlong and out-of-horizon windows", () => {
  for (const selection of [
    { period: "Day", date: "2026-02-29" }, { period: "Day", date: "1999-12-31" },
    { period: "Week", date: "2000-01-06" }, { period: "Day", date: addDays(today, 367) },
    { period: "Custom", from: "2026-10-09", through: "2026-10-08" },
    { period: "Custom", from: "2024-01-01", through: "2025-01-01" },
    { period: "CalendarMonth", date: addDays(today, 366) }
  ] as EnergySelection[]) assert.ok(energySelectionError(selection, today), JSON.stringify(selection));
  assert.equal(energySelectionError({ period: "Day", date: addDays(today, 366) }, today), null);
  assert.equal(energySelectionError({ period: "Custom", from: "2024-01-01", through: "2024-12-31" }, today), null);
});
test("native energy arrows move full weeks, 30-day windows, months and custom spans across DST", () => {
  assert.deepEqual(moveEnergySelection({ period: "Week", date: "2026-03-29" }, today, 1), { period: "Week", date: "2026-04-05" });
  assert.deepEqual(moveEnergySelection({ period: "RollingMonth", date: "2026-10-25" }, today, 1), { period: "RollingMonth", date: "2026-11-24" });
  assert.deepEqual(moveEnergySelection({ period: "CalendarMonth", date: "2024-01-31" }, today, 1), { period: "CalendarMonth", date: "2024-02-01" });
  assert.deepEqual(moveEnergySelection({ period: "Custom", date: "2026-10-24", from: "2026-10-24", through: "2026-10-26" }, today, 1),
    { period: "Custom", date: "2026-10-27", from: "2026-10-27", through: "2026-10-29" });
  assert.equal(canMoveEnergySelection({ period: "Day", date: addDays(today, 366) }, today, 1), false);
  assert.equal(canMoveEnergySelection({ period: "Day", date: "2000-01-01" }, today, -1), false);
  assert.deepEqual(chooseEnergyPeriod({ period: "CalendarMonth", date: "2024-02-01" }, "Custom", today),
    { period: "Custom", date: "2024-02-01", from: "2024-02-01", through: "2024-02-29" });
});

type Call = { method: "production" | "sales" | "recheck"; args: any[] };
function prepare(calls: Call[]) {
  const state = createDemoState(now);
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  globals.__smartUi = { auth: { isDemo: false, api: {
    sessionEpoch: 0, onSessionChange: () => () => {},
    getDashboard: async () => ({ inverter: null, timeZoneId: "UTC" }),
    getSolarEstimate: async () => null,
    getSiteSettings: async () => ({ ...state.site, solarSales: { ...state.site.solarSales, timeZoneId: "UTC" } }),
    getProduction: async (...args: any[]) => { calls.push({ method: "production", args }); return demoProduction(args[0], args[1], now, "UTC", args[3]); },
    getSalesDetails: async (...args: any[]) => { calls.push({ method: "sales", args }); return demoSales(args[0], args[1], now, "UTC", args[3], args[4]?.includeUpcoming); }
  } }, route: { params: {} } };
}
function press(root: ReactTestInstance, label: string) { return root.findAllByType("Pressable").find(button => button.props.accessibilityLabel === label || (!button.props.accessibilityLabel && button.findAllByType("Text").some(text => text.props.children === label)))!; }
function input(root: ReactTestInstance, label: string) { return root.findAllByType("TextInput").find(field => field.props.accessibilityLabel === label)!; }
async function tap(root: ReactTestInstance, label: string) { await act(async () => { press(root, label).props.onPress(); }); }
async function type(root: ReactTestInstance, label: string, value: string) { await act(async () => { input(root, label).props.onChangeText(value); }); }
async function close(renderer?: ReturnType<typeof create>) { await act(async () => renderer?.unmount()); delete globals.__smartUi; delete globals.IS_REACT_ACT_ENVIRONMENT; }

test("actual native Energy tabs preserve calendar month, custom dates and all five shared controls", async () => {
  const calls: Call[] = []; prepare(calls); let renderer: ReturnType<typeof create> | undefined;
  try {
    const { EnergyScreen } = await uiHarness('export { EnergyScreen } from "./src/features/energy/EnergyScreen";');
    await act(async () => { renderer = create(React.createElement(EnergyScreen!)); });
    const root = renderer!.root;
    for (const label of ["Day", "7 days", "30 days", "Month", "Custom"]) assert.ok(press(root, label));
    await tap(root, "Month");
    assert.equal(input(root, "Month").props.value, "2026-10");
    // Applying an unchanged month also produces a valid date, without appending twice.
    await tap(root, "Apply");
    assert.deepEqual(calls.at(-1)!.args.slice(0, 2), ["CalendarMonth", "2026-10-01"]);
    await type(root, "Month", "2024-02"); await tap(root, "Apply");
    await tap(root, "Export");
    assert.equal(input(root, "Month").props.value, "2024-02");
    const month = calls.filter(call => call.method === "sales").at(-1)!;
    assert.deepEqual(month.args.slice(0, 2), ["Month", "2024-02-01"]); assert.deepEqual(month.args[4], { includeUpcoming: true });
    await tap(root, "Custom");
    assert.equal(input(root, "From").props.value, "2024-02-01"); assert.equal(input(root, "To (inclusive)").props.value, "2024-02-29");
    await type(root, "From", "2026-10-10"); await type(root, "To (inclusive)", "2026-10-16"); await tap(root, "Apply");
    await tap(root, "Production");
    assert.equal(input(root, "From").props.value, "2026-10-10"); assert.equal(input(root, "To (inclusive)").props.value, "2026-10-16");
    const custom = calls.filter(call => call.method === "production").at(-1)!;
    assert.deepEqual(custom.args.slice(0, 2), ["Custom", "2026-10-10"]); assert.deepEqual(custom.args[3], { from: "2026-10-10", through: "2026-10-16" });
    // Editor drafts survive a tab switch without submitting a different period.
    await type(root, "From", "2026-10-11"); await tap(root, "Export");
    assert.equal(input(root, "From").props.value, "2026-10-11");
    assert.deepEqual(calls.filter(call => call.method === "sales").at(-1)!.args[3], { from: "2026-10-10", through: "2026-10-16" });
    for (const [label, period] of [["Day", "Day"], ["7 days", "Week"], ["30 days", "RollingMonth"], ["Month", "Month"], ["Custom", "Custom"]]) {
      await tap(root, label!); assert.equal(calls.filter(call => call.method === "sales").at(-1)!.args[0], period);
      assert.equal(press(root, label!).props.accessibilityState.selected, true);
    }
  } finally { await close(renderer); }
});

test("changing native energy tabs and Today pin the displayed local day", async () => {
  const calls: Call[] = []; prepare(calls); let renderer: ReturnType<typeof create> | undefined;
  try {
    const { EnergyScreen } = await uiHarness('export { EnergyScreen } from "./src/features/energy/EnergyScreen";');
    await act(async () => { renderer = create(React.createElement(EnergyScreen!)); });
    const root = renderer!.root;
    await tap(root, "Export");
    assert.deepEqual(calls.filter(call => call.method === "sales").at(-1)!.args.slice(0, 2), ["Day", today]);
    await tap(root, "Production");
    assert.deepEqual(calls.filter(call => call.method === "production").at(-1)!.args.slice(0, 2), ["Today", today]);
    await tap(root, "Next 7 days"); await tap(root, "Today"); await tap(root, "Export");
    assert.deepEqual(calls.filter(call => call.method === "sales").at(-1)!.args.slice(0, 2), ["Day", today]);
  } finally { await close(renderer); }
});

test("actual native future shortcuts and arrows keep forecast gaps and export unavailable", async () => {
  const calls: Call[] = []; prepare(calls); let renderer: ReturnType<typeof create> | undefined;
  try {
    const { EnergyScreen, ProductionChart } = await uiHarness('export { EnergyScreen } from "./src/features/energy/EnergyScreen"; export { ProductionChart } from "./src/ui/charts/ProductionChart";');
    await act(async () => { renderer = create(React.createElement(EnergyScreen!)); });
    const root = renderer!.root;
    await tap(root, "Next 7 days");
    assert.deepEqual(calls.filter(call => call.method === "production").at(-1)!.args.slice(0, 2), ["Week", "2026-10-14"]);
    await tap(root, "Next period");
    assert.deepEqual(calls.filter(call => call.method === "production").at(-1)!.args.slice(0, 2), ["Week", "2026-10-21"]);
    await tap(root, "Next 30 days");
    const request = calls.filter(call => call.method === "production").at(-1)!;
    assert.deepEqual(request.args.slice(0, 2), ["Month", "2026-11-06"]);
    const points = root.findByType(ProductionChart!).props.points;
    assert.equal(points.length, 30);
    assert.ok(points.slice(1).every((point: any) => point.actual === null));
    assert.ok(points.slice(0, 16).every((point: any) => typeof point.expected === "number"));
    assert.ok(points.slice(16).every((point: any) => point.expected === null && point.possible === null));
    const texts = root.findAllByType("Text").map(text => text.props.children).filter(value => typeof value === "string");
    assert.ok(texts.includes("Weather data is available only for part of this period. Dates without a forecast remain unavailable."));
    assert.ok(texts.includes("Total for available forecast days")); assert.ok(texts.includes("Expected best hour"));
    await tap(root, "How it works");
    assert.ok(root.findAllByType("Text").some(text => text.props.children === "Weather forecasts cover up to 16 days including today. Later dates remain unavailable."));
    await tap(root, "Export");
    const exported = calls.filter(call => call.method === "sales").at(-1)!;
    assert.deepEqual(exported.args.slice(0, 2), ["RollingMonth", "2026-11-06"]);
    assert.deepEqual(exported.args[4], { includeUpcoming: true });
    assert.ok(root.findAllByType("Text").some(text => text.props.children === "Future export and value are unavailable until measured. View the generation forecast on the Production tab."));
    await tap(root, "Next period");
    assert.deepEqual(calls.filter(call => call.method === "sales").at(-1)!.args.slice(0, 2), ["RollingMonth", "2026-12-06"]);
    const hero = root.findAllByType("Text").find(text => [text.props.style].flat(Infinity).some(style => style?.fontSize === 52))!;
    assert.equal(hero.props.children, "— kWh");
  } finally { await close(renderer); }
});

test("native price checks use only measured dates then reload the original future window", async () => {
  const calls: Call[] = []; prepare(calls); let renderer: ReturnType<typeof create> | undefined;
  const api = globals.__smartUi!.auth.api;
  api.getSalesDetails = async (...args: any[]) => {
    calls.push({ method: "sales", args });
    return { ...demoSales(args[0], args[1], now, "UTC", args[3], args[4]?.includeUpcoming), priceError: "Prices could not be checked." };
  };
  api.recheckSalesPrices = async (...args: any[]) => { calls.push({ method: "recheck", args }); return null; };
  try {
    const { EnergyScreen } = await uiHarness('export { EnergyScreen } from "./src/features/energy/EnergyScreen";');
    await act(async () => { renderer = create(React.createElement(EnergyScreen!)); });
    const root = renderer!.root;
    await tap(root, "Month"); await tap(root, "Export");
    await tap(root, "Check prices again");
    const recheck = calls.find(call => call.method === "recheck")!;
    assert.deepEqual(recheck.args.slice(0, 3), ["Custom", "2026-10-01", { from: "2026-10-01", through: today }]);
    assert.equal(recheck.args.length, 4);
    const refreshed = calls.filter(call => call.method === "sales").at(-1)!;
    assert.deepEqual(refreshed.args.slice(0, 2), ["Month", "2026-10-01"]);
    assert.deepEqual(refreshed.args[4], { includeUpcoming: true });
    assert.equal(input(root, "Month").props.value, "2026-10");
    await tap(root, "Day"); await type(root, "Date", "2026-10-10"); await tap(root, "Apply");
    assert.equal(press(root, "Check prices again").props.disabled, true);
    assert.equal(calls.filter(call => call.method === "recheck").length, 1);
  } finally { await close(renderer); }
});

test("shared native Russian filters use the real catalog and a horizontal row with reachable labels", async () => {
  const catalog = JSON.parse(readFileSync(new URL("../../i18n/ru.json", import.meta.url), "utf8"));
  const labels = ["Day", "7 days", "30 days", "Month", "Custom", "Chart period"];
  const translated = Object.fromEntries(labels.map(label => [label, catalog[label]]));
  globals.IS_REACT_ACT_ENVIRONMENT = true; globals.__smartUi = { auth: {} }; let renderer: ReturnType<typeof create> | undefined;
  try {
    const { EnergyPeriodFilters } = await uiHarness('export { EnergyPeriodFilters } from "./src/features/energy/EnergyPeriodFilters";', {
      stubs: { language: `const labels=${JSON.stringify(translated)};export const useLanguage=()=>({language:"ru",t:(phrase)=>labels[phrase]??phrase});` }
    });
    let selected: EnergySelection = { period: "Day" };
    await act(async () => { renderer = create(React.createElement(EnergyPeriodFilters!, { selection: selected, today, onChange: (value: EnergySelection) => { selected = value; } })); });
    assert.equal(renderer!.root.findByType("ScrollView").props.horizontal, true);
    for (const label of labels.slice(0, 5)) { const button = press(renderer!.root, catalog[label]); assert.ok(button); assert.ok(button.props.style.minHeight >= 44); await act(async () => button.props.onPress()); }
    assert.equal(selected.period, "Custom"); assert.equal(catalog.Custom, "Пользовательский");
  } finally { await close(renderer); }
});
