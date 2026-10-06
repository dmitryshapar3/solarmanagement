import assert from "node:assert/strict";
import { test } from "node:test";
import React from "react";
import { act, create } from "react-test-renderer";
import { ApiClient } from "../src/core/api/ApiClient";
import { IntegrationApi, type SocketCommandReceipt } from "../src/core/api/IntegrationApi";
import { SocketCommandCoordinator } from "../src/core/api/SocketCommandCoordinator";
import type { Device } from "../src/core/api/types";
import type { AccountPermissions } from "../src/core/api/AccountSecurityApi";
import { deviceComponents } from "./deviceComponentHarness";
const globals = globalThis as typeof globalThis & { __deviceUiAuth?: unknown; IS_REACT_ACT_ENVIRONMENT?: boolean };
async function harness(lostResponse: boolean, controlled = false, initialPermissions: AccountPermissions["permissions"] | undefined = ["ControlDevices", "ManageRules"], canSwitch: boolean | null = true) {
  const calls: { path: string; method: string; body: any }[] = []; let inventoryReads = 0;
  const receipt: SocketCommandReceipt = { deviceId: "socket-a", commandId: "fixed-command", isOn: true, status: "pending", rejection: null, createdAt: "2026-10-04T10:00:00Z", completedAt: null };
  const client = new ApiClient({ baseUrl: "https://solar.example", token: "owner", transport: async (url, init) => {
    const route = new URL(url).pathname; calls.push({ path: route, method: init.method, body: init.body ? JSON.parse(init.body) : null }); if (init.method === "POST" && lostResponse) throw new Error("Lost response");
    const result = route === "/api/devices" ? { devices: [ { id: "socket-a", name: "Heater", category: null, online: true, stateKnown: true, isOn: ++inventoryReads > 1, currentPowerW: 0 }, { id: "socket-b", name: "Neighbor", category: null, online: true, stateKnown: true, isOn: false, currentPowerW: 0 } ], lastUpdated: null } : init.method === "POST" ? receipt : route.endsWith("/fixed-command") ? { ...receipt, status: "acknowledged" } : [];
    return { status: 200, ok: true, text: async () => JSON.stringify(result) };
  } });
  const api = { get sessionEpoch() { return client.sessionEpoch; }, onSessionChange: (observer: () => void) => client.onSessionChange(observer), socketCommands: new SocketCommandCoordinator(new IntegrationApi(client), () => "fixed-command"), getDevices: () => client.request<{ devices: Device[] }>("/api/devices"), setDeviceState: async () => assert.fail("A real session cannot issue the v1 command.") };
  globals.__deviceUiAuth = { api, isDemo: false }; globals.IS_REACT_ACT_ENVIRONMENT = true;
  const { DeviceRow } = await deviceComponents(); let inventory = (await api.getDevices()).devices; let redraw!: () => void; const controllingRules = controlled ? [{ id: 12, name: "Keep warm", enabled: true, intervalSeconds: 30 }] : [];
  let permissions = initialPermissions;
  function Rows() { const [, update] = React.useState(0); redraw = () => update(value => value + 1); return React.createElement(React.Fragment, null, ...inventory.map(device => React.createElement(DeviceRow, { key: device.id, device, permissions, details: canSwitch === null ? undefined : { canSwitch, controllingRules: device.id === "socket-a" ? controllingRules : [], providerId: "fixture" }, onChanged: async () => { inventory = (await api.getDevices()).devices; redraw(); } }))); }
  let renderer!: ReturnType<typeof create>; await act(async () => { renderer = create(React.createElement(Rows)); }); const card = (name: string) => renderer.root.findAllByType("Card").find(item => item.findAllByType("Text").some(text => text.props.children === name))!;
  return { calls, renderer, get inventoryReads() { return inventoryReads; }, card, setPermissions: async (next: AccountPermissions["permissions"] | undefined) => { permissions = next; await act(async () => redraw()); }, toggle: (name: string) => card(name).findByType("NativeSwitch"), button: (name: string, label: string) => card(name).findAllByType("button").find(item => item.props.label === label)!, close: async () => { await act(async () => renderer.unmount()); delete globals.__deviceUiAuth; delete globals.IS_REACT_ACT_ENVIRONMENT; } };
}
for (const lostResponse of [false, true]) test(`device row preserves observed OFF for ${lostResponse ? "an uncertain response" : "a pending receipt"} until matching acknowledgement`, async () => {
  const h = await harness(lostResponse); try {
    await act(async () => { h.toggle("Heater").props.onValueChange(true); }); assert.equal(h.inventoryReads, 1); assert.equal(h.toggle("Heater").props.value, false); assert.equal(h.toggle("Heater").props.disabled, true); assert.equal(h.card("Heater").findAllByType("button").some(item => item.props.label === "Allow another command"), lostResponse);
    if (lostResponse) assert.match(h.card("Heater").findAllByType("Text").map(item => String(item.props.children)).join(" "), /may still finish.*does not cancel/);
    assert.equal(h.toggle("Neighbor").props.disabled, false); assert.equal(h.calls.filter(call => call.method === "POST").length, 1); assert.deepEqual(h.calls.find(call => call.method === "POST")!.body, { commandId: "fixed-command", isOn: true }); assert.doesNotMatch(h.card("Heater").findAllByType("Text").map(item => String(item.props.children)).join(" "), /switched on/i);
    await act(async () => { h.button("Heater", "Check command result").props.onPress(); }); assert.equal(h.inventoryReads, 2); assert.equal(h.toggle("Heater").props.value, true); assert.equal(h.toggle("Neighbor").props.value, false); assert.equal(h.calls.filter(call => call.method === "POST").length, 1); assert.equal(h.toggle("Heater").props.disabled, false);
  } finally { await h.close(); }
});
for (const choice of ["pause", "once", "cancel"] as const) test(`a controlled device requires explicit ${choice} selection before a manual command`, async () => {
  const h = await harness(false, true); try {
    await act(async () => { h.toggle("Heater").props.onValueChange(true); }); assert.equal(h.calls.filter(call => call.method === "POST").length, 0); assert.equal(h.toggle("Heater").props.value, false); const label = choice === "pause" ? "Pause Keep warm" : choice === "once" ? "Just this once" : "Cancel"; await act(async () => { h.button("Heater", label).props.onPress(); });
    const posts = h.calls.filter(call => call.method === "POST"); assert.equal(posts.length, choice === "cancel" ? 0 : 1); if (choice !== "cancel") assert.deepEqual(posts[0]!.body, { commandId: "fixed-command", isOn: true, onRuleConflict: choice }); assert.equal(h.toggle("Heater").props.value, false); assert.equal(h.card("Heater").findAllByType("Modal").length, 0);
  } finally { await h.close(); }
});
test("read-only and unknown permissions cannot switch a capable device; missing capability stays disabled", async () => {
  for (const [permissions, capability] of [[ ["Read"], true ], [ [], true ], [ ["ControlDevices"], false ], [ ["ControlDevices"], null ]] as const) {
    const h = await harness(false, false, [...permissions], capability); try {
      assert.equal(h.toggle("Heater").props.disabled, true);
      await act(async () => { h.toggle("Heater").props.onValueChange(true); });
      assert.equal(h.calls.filter(call => call.method === "POST").length, 0);
    } finally { await h.close(); }
  }
});
test("control-only members can choose once or cancel but cannot pause automations", async () => {
  const h = await harness(false, true, ["Read", "ControlDevices"]); try {
    await act(async () => { h.toggle("Heater").props.onValueChange(true); });
    assert.equal(h.button("Heater", "Pause Keep warm").props.disabled, true);
    assert.equal(h.button("Heater", "Just this once").props.disabled, false);
    assert.equal(h.button("Heater", "Cancel").props.disabled, false);
    await act(async () => { h.button("Heater", "Pause Keep warm").props.onPress(); });
    assert.equal(h.calls.filter(call => call.method === "POST").length, 0);
    await act(async () => { h.button("Heater", "Just this once").props.onPress(); });
    assert.deepEqual(h.calls.find(call => call.method === "POST")!.body, { commandId: "fixed-command", isOn: true, onRuleConflict: "once" });
  } finally { await h.close(); }
});
test("callbacks captured before permission removal cannot switch or pause after a refresh", async () => {
  const h = await harness(false, true); try {
    const oldSwitch = h.toggle("Heater").props.onValueChange;
    await act(async () => { oldSwitch(true); });
    const oldPause = h.button("Heater", "Pause Keep warm").props.onPress;
    await h.setPermissions(["Read"]);
    assert.equal(h.toggle("Heater").props.disabled, true);
    await act(async () => { oldSwitch(true); oldPause(); });
    assert.equal(h.calls.filter(call => call.method === "POST").length, 0);
    assert.equal(h.button("Heater", "Cancel").props.disabled, false);
    await act(async () => { h.button("Heater", "Cancel").props.onPress(); });
    assert.equal(h.card("Heater").findAllByType("Modal").length, 0);
  } finally { await h.close(); }
});
