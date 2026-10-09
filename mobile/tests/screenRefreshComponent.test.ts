import assert from "node:assert/strict";
import { test } from "node:test";
import React from "react";
import { act, create } from "react-test-renderer";
import { createDemoState } from "../src/features/demo/fixtures";
import { globals, uiHarness } from "./support/uiHarness";
type Call = { method: string; args: unknown[] };
async function screens() { return uiHarness(`export { DashboardScreen } from "./src/features/dashboard/DashboardScreen"; export { EnergyScreen } from "./src/features/energy/EnergyScreen"; export { HourlyTableSheet } from "./src/features/generation/HourlyTableSheet"; export { ExportHourlySheet } from "./src/features/sales/ExportHourlySheet"; export { SettingsScreen } from "./src/features/settings/SettingsScreen";`); }
function mockApi(calls: Call[], delayedProduction?: () => Promise<unknown>) {
  const state = createDemoState(new Date()); const dashboard = { inverter: null, manualDevices: [], devices: [], rules: [], timeZoneId: "Europe/Warsaw" }; let productionReads = 0;
  const record = (method: string, args: unknown[], value: unknown) => { calls.push({ method, args }); return Promise.resolve(value); };
  return { sessionEpoch: 0, onSessionChange: () => () => {}, socketCommands: { sessionEpoch: 0, subscribe: () => () => {}, get: () => null, isRunning: () => false },
    getDashboard: (...args: unknown[]) => record("dashboard", args, dashboard), refreshDashboard: (...args: unknown[]) => record("inverter", args, dashboard), getSolarEstimate: (...args: unknown[]) => record("estimate", args, null),
    getProduction: (...args: unknown[]) => { calls.push({ method: "production", args }); return ++productionReads > 1 && delayedProduction ? delayedProduction() : Promise.resolve(null); },
    getActivity: (...args: unknown[]) => record("activity", args, null), getSiteSettings: (...args: unknown[]) => record("site", args, state.site), getSales: (...args: unknown[]) => record("sales", args, null), getSalesDetails: (...args: unknown[]) => record("sales", args, null),
    accountSecurity: { getPermissions: (...args: unknown[]) => record("permissions", args, { role: "Owner", permissions: ["Read", "ControlDevices", "ManageRules"] }) }, integrations: { getSocketSources: (...args: unknown[]) => record("sources", args, [{ id: "fixture", name: "Inverter", isDefault: true }]) }, request: (...args: unknown[]) => record("account", args, { displayName: "Fixture", verifiedEmail: null, verifiedPhone: null }) };
}
function control(renderer: ReturnType<typeof create>) { return renderer.root.findAllByType("ScrollView").find(view => view.props.refreshControl)!.props.refreshControl; }
function prepare(api: unknown, route = { params: {} }) { globals.IS_REACT_ACT_ENVIRONMENT = true; globals.__smartUi = { auth: { api, isDemo: true, apiBaseUrl: "https://solar.example", username: "Fixture", logout: async () => {} }, route }; }
async function close(renderer?: ReturnType<typeof create>) { await act(async () => renderer?.unmount()); delete globals.__smartUi; delete globals.IS_REACT_ACT_ENVIRONMENT; }

test("Home pull refresh reloads every resource once and waits for the slowest card", async () => {
  const calls: Call[] = []; let finish!: (value: unknown) => void; const slow = new Promise(resolve => { finish = resolve; }); prepare(mockApi(calls, () => slow)); let renderer: ReturnType<typeof create> | undefined;
  try { const Component = (await screens()).DashboardScreen!; await act(async () => { renderer = create(React.createElement(Component)); });
    assert.deepEqual(calls.map(call => call.method).sort(), ["activity", "dashboard", "estimate", "permissions", "production", "sales", "site", "site", "sources"]);
    assert.equal(renderer!.root.findByType("ScrollView").props.alwaysBounceVertical, true); assert.equal(renderer!.root.findAllByType("Pressable").some(button => /Refresh|Retry/.test(button.props.accessibilityLabel ?? "")), false);
    calls.length = 0; const pull = control(renderer!).props.onRefresh; await act(async () => { pull(); pull(); });
    assert.deepEqual(calls.map(call => call.method).sort(), ["activity", "estimate", "inverter", "permissions", "production", "sales", "site", "site", "sources"]); assert.equal(control(renderer!).props.refreshing, true);
    await act(async () => { control(renderer!).props.onRefresh(); }); assert.equal(calls.length, 9);
    await act(async () => { finish(null); await slow; }); assert.equal(control(renderer!).props.refreshing, false);
  } finally { await close(renderer); }
});

test("Energy production and hourly sheet pulls preserve the selected period and date", async () => {
  const components = await screens(); let renderer: ReturnType<typeof create> | undefined;
  try { for (const name of ["EnergyScreen", "HourlyTableSheet"]) { const calls: Call[] = []; prepare(mockApi(calls), { params: { period: "Month", date: "2026-10-04" } }); await act(async () => { renderer = create(React.createElement(components[name]!)); });
    if (name === "EnergyScreen") await act(async () => { renderer!.root.findAllByType("Pressable").find(button => button.props.accessibilityLabel === "7 days")!.props.onPress(); });
    const selected = calls.filter(call => call.method === "production").at(-1)!.args.slice(0, 2); calls.length = 0;
    await act(async () => { control(renderer!).props.onRefresh(); }); assert.deepEqual(calls.map(call => call.method).sort(), name === "EnergyScreen" ? ["estimate", "inverter", "production"] : ["production"]); assert.deepEqual(calls.find(call => call.method === "production")!.args.slice(0, 2), selected); assert.equal(control(renderer!).props.refreshing, false);
    await close(renderer); renderer = undefined;
  } } finally { await close(renderer); }
});

test("Energy export and hourly export pulls preserve their reporting window", async () => {
  const components = await screens(); let renderer: ReturnType<typeof create> | undefined;
  try { for (const name of ["EnergyScreen", "ExportHourlySheet"]) { const calls: Call[] = []; prepare(mockApi(calls), { params: { period: "Month", date: "2026-09-01", segment: "Export" } }); await act(async () => { renderer = create(React.createElement(components[name]!)); });
    if (name === "EnergyScreen") await act(async () => { renderer!.root.findAllByType("Pressable").find(button => button.props.accessibilityLabel === "Month")!.props.onPress(); });
    const selected = calls.filter(call => call.method === "sales").at(-1)!.args.slice(0, 2); calls.length = 0;
    await act(async () => { control(renderer!).props.onRefresh(); }); assert.deepEqual(calls.map(call => call.method).sort(), name === "EnergyScreen" ? ["inverter", "sales", "site"] : ["sales", "site"]); assert.deepEqual(calls.find(call => call.method === "sales")!.args.slice(0, 2), selected);
    await close(renderer); renderer = undefined;
  } } finally { await close(renderer); }
});

test("Settings block pulls during the initial account read and recover errors through the same gesture", async () => {
  const calls: Call[] = []; let rejectInitial!: (error: Error) => void; const initial = new Promise((_, reject) => { rejectInitial = reject; }); const api = mockApi(calls); api.request = (...args) => { calls.push({ method: "account", args }); return calls.filter(call => call.method === "account").length === 1 ? initial : Promise.resolve({ displayName: "Fixture", verifiedEmail: null, verifiedPhone: null }); }; prepare(api); globals.__smartUi!.auth.isDemo = false; let renderer: ReturnType<typeof create> | undefined;
  try { const Component = (await screens()).SettingsScreen!; await act(async () => { renderer = create(React.createElement(Component)); }); assert.equal(control(renderer!).props.refreshing, true);
    await act(async () => { control(renderer!).props.onRefresh(); }); assert.equal(calls.length, 1);
    await act(async () => { rejectInitial(new Error("Temporary account outage")); }); assert.equal(control(renderer!).props.refreshing, false); assert.equal(renderer!.root.findByType("ScrollView").props.alwaysBounceVertical, true);
    assert.equal(renderer!.root.findAllByType("Pressable").some(button => /Refresh|Retry/.test(button.props.accessibilityLabel ?? "")), false);
    await act(async () => { control(renderer!).props.onRefresh(); }); assert.equal(calls.length, 2); assert.equal(control(renderer!).props.refreshing, false);
  } finally { await close(renderer); }
});
