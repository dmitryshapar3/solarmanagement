import assert from "node:assert/strict";
import { test } from "node:test";
import React from "react";
import { act, create } from "react-test-renderer";
import type { Device } from "../src/core/api/types";
import { deviceComponents } from "./deviceComponentHarness";
function deferred<T>() { let resolve!: (value: T) => void; return { promise: new Promise<T>(done => { resolve = done; }), resolve: (value: T) => resolve(value) }; }
test("the device sheet fences late name saves and keeps the replacement account's busy draft", async () => {
  const device: Device = { id: "8d62e8e7-763a-44d5-9a75-0637a3d66d36", name: "Old provider", category: null, online: true, isOn: false, stateKnown: true, currentPowerW: 0, cloudName: "Provider", localName: null };
  const old = deferred<Device>(), current = deferred<Device>();
  const makeApi = (name: string, save: Promise<Device>) => {
    let stored = { ...device, name };
    return { sessionEpoch: 0, onSessionChange: () => () => {}, accountSecurity: { getPermissions: async () => ({ role: "Owner", permissions: ["Read", "ControlDevices", "ManageRules"] }) }, getDeviceDetails: async () => ({ id: device.id, name: stored.name, device: stored, controllingRules: [], canSwitch: false, supportsHistory: false, providerId: "fixture", instanceId: null }), renameDevice: async () => { stored = await save; return stored; }, socketCommands: { sessionEpoch: 0, subscribe: () => () => {}, get: () => null, isRunning: () => false } };
  };
  const state = { api: makeApi("Old provider", old.promise), isDemo: false };
  const globals = globalThis as typeof globalThis & { __deviceUiAuth?: typeof state; IS_REACT_ACT_ENVIRONMENT?: boolean }; globals.__deviceUiAuth = state; globals.IS_REACT_ACT_ENVIRONMENT = true;
  let renderer: ReturnType<typeof create> | undefined;
  try {
    const { DeviceSheetScreen: Component } = await deviceComponents(); const props = { route: { params: { id: device.id } }, navigation: { goBack() {}, navigate() {} } };
    await act(async () => { renderer = create(React.createElement(Component, props)); });
    const button = (label: string) => renderer!.root.findAllByType("button").find(item => item.props.label === label)!;
    const edit = () => renderer!.root.findAllByType("NavigationRow").find(item => item.props.title === "Name in SmartSolar")!.props.onPress();
    const field = () => renderer!.root.findByType("TextField");
    await act(async () => { edit(); }); await act(async () => { field().props.onChangeText("Old account draft"); }); await act(async () => { button("Save name").props.onPress(); }); assert.equal(button("Save name").props.loading, true);
    state.api = makeApi("Current provider", current.promise); await act(async () => { renderer!.update(React.createElement(Component, props)); });
    await act(async () => { edit(); }); await act(async () => { field().props.onChangeText("Current draft"); }); await act(async () => { button("Save name").props.onPress(); });
    await act(async () => { old.resolve({ ...device, name: "Old account draft" }); await old.promise; });
    assert.equal(field().props.value, "Current draft"); assert.equal(field().props.editable, false); assert.equal(button("Save name").props.loading, true, "A previous account's completion cannot release the current save"); assert.equal(renderer!.root.findAllByType("NavigationRow").find(item => item.props.title === "Name in SmartSolar")!.props.value, "Current provider");
    await act(async () => { current.resolve({ ...device, name: "Current draft" }); await current.promise; });
    assert.equal(renderer!.root.findAllByType("TextField").length, 0); assert.equal(renderer!.root.findAllByType("NavigationRow").find(item => item.props.title === "Name in SmartSolar")!.props.value, "Current draft"); assert.equal(renderer!.root.findByType("ErrorBanner").props.message, null);
  } finally { await act(async () => { renderer?.unmount(); }); delete globals.__deviceUiAuth; delete globals.IS_REACT_ACT_ENVIRONMENT; }
});

test("device controls reject late previous-account permissions and callbacks while the replacement remains read-only", async () => {
  const device: Device = { id: "fixture-device", name: "Fixture", category: null, online: true, isOn: false, stateKnown: true, currentPowerW: 0 };
  const oldPermissions = deferred<{ role: string; permissions: string[] }>(); let commands = 0;
  const makeApi = (permissions: () => Promise<{ role: string; permissions: string[] }>) => ({ sessionEpoch: 0, onSessionChange: () => () => {}, accountSecurity: { getPermissions: permissions }, getDeviceDetails: async () => ({ id: device.id, name: device.name, device, controllingRules: [], canSwitch: true, supportsHistory: false, instanceId: null }), socketCommands: { sessionEpoch: 0, subscribe: () => () => {}, get: () => null, isRunning: () => false, send: async () => { commands++; return { status: "acknowledged" }; } } });
  let reads = 0; const oldApi = makeApi(async () => ++reads === 1 ? { role: "Owner", permissions: ["Read", "ControlDevices", "ManageRules"] } : oldPermissions.promise);
  const state = { api: oldApi, isDemo: false };
  const globals = globalThis as typeof globalThis & { __deviceUiAuth?: typeof state; IS_REACT_ACT_ENVIRONMENT?: boolean }; globals.__deviceUiAuth = state; globals.IS_REACT_ACT_ENVIRONMENT = true;
  let renderer: ReturnType<typeof create> | undefined;
  try {
    const { DeviceSheetScreen: Component } = await deviceComponents(); const props = { route: { params: { id: device.id } }, navigation: { goBack() {}, navigate() {} } };
    await act(async () => { renderer = create(React.createElement(Component, props)); });
    const button = () => renderer!.root.findAllByType("button").find(item => item.props.label === "On")!;
    assert.equal(button().props.disabled, false); const oldPress = button().props.onPress;
    await act(async () => { renderer!.root.findByType("Screen").props.onRefresh(); });
    assert.equal(button().props.disabled, true, "pending permission reads block retained controls");
    state.api = makeApi(async () => ({ role: "Viewer", permissions: ["Read"] }));
    await act(async () => { renderer!.update(React.createElement(Component, props)); });
    assert.equal(button().props.disabled, true);
    await act(async () => { oldPermissions.resolve({ role: "Owner", permissions: ["Read", "ControlDevices", "ManageRules"] }); await oldPermissions.promise; oldPress(); });
    assert.equal(button().props.disabled, true, "old owner permissions cannot authorize the replacement account");
    assert.equal(commands, 0, "a captured previous-account callback must not submit a command");
  } finally { await act(async () => renderer?.unmount()); delete globals.__deviceUiAuth; delete globals.IS_REACT_ACT_ENVIRONMENT; }
});

test("a failed permission refresh disables retained device controls and stale callbacks", async () => {
  const device: Device = { id: "fixture-device", name: "Fixture", category: null, online: true, isOn: false, stateKnown: true, currentPowerW: 0 };
  let reads = 0, commands = 0;
  const api = { sessionEpoch: 0, onSessionChange: () => () => {}, accountSecurity: { getPermissions: async () => { if (++reads > 1) throw new Error("Permission read unavailable"); return { role: "Owner", permissions: ["Read", "ControlDevices", "ManageRules"] }; } }, getDeviceDetails: async () => ({ id: device.id, name: device.name, device, controllingRules: [], canSwitch: true, supportsHistory: false, instanceId: null }), socketCommands: { sessionEpoch: 0, subscribe: () => () => {}, get: () => null, isRunning: () => false, send: async () => { commands++; return { status: "acknowledged" }; } } };
  const globals = globalThis as typeof globalThis & { __deviceUiAuth?: unknown; IS_REACT_ACT_ENVIRONMENT?: boolean }; globals.__deviceUiAuth = { api, isDemo: false }; globals.IS_REACT_ACT_ENVIRONMENT = true;
  let renderer: ReturnType<typeof create> | undefined;
  try {
    const { DeviceSheetScreen: Component } = await deviceComponents();
    await act(async () => { renderer = create(React.createElement(Component, { route: { params: { id: device.id } }, navigation: { goBack() {}, navigate() {} } })); });
    const button = () => renderer!.root.findAllByType("button").find(item => item.props.label === "On")!; assert.equal(button().props.disabled, false); const oldPress = button().props.onPress;
    await act(async () => { await renderer!.root.findByType("Screen").props.onRefresh(); });
    assert.equal(button().props.disabled, true); assert.match(renderer!.root.findByType("ErrorBanner").props.message, /Permission read unavailable/);
    await act(async () => { oldPress(); button().props.onPress(); }); assert.equal(commands, 0);
  } finally { await act(async () => renderer?.unmount()); delete globals.__deviceUiAuth; delete globals.IS_REACT_ACT_ENVIRONMENT; }
});
