import assert from "node:assert/strict";
import { test } from "node:test";
import { createRequire } from "node:module";
import path from "node:path";
import React from "react";
import { act, create } from "react-test-renderer";
import { build } from "esbuild";
import { ApiClient } from "../src/core/api/ApiClient";
import { DeyeSolarApi } from "../src/core/api/DeyeSolarApi";
import type { Rule } from "../src/core/api/types";

import { globals as uiGlobals, uiHarness } from "./support/uiHarness";
async function component() {
  const components = await uiHarness('export { RuleEditorScreen } from "./src/features/rules/RuleEditorScreen";', { stubComponents: true, stubs: { SelectField: 'export const SelectField="SelectField";', ThresholdRange: 'export const ThresholdRange="ThresholdRange";' } });
  return components.RuleEditorScreen!;
}

for (const { source, defaultAvailable } of [{ source: "battery-inverter", defaultAvailable: true },
  { source: null, defaultAvailable: true }, { source: null, defaultAvailable: false }]) {
  test(`the real rule editor saves source ${source ?? (defaultAvailable ? "installation default" : "socket link without a default")} without changing its socket`, async () => {
    const rule: Rule = {
      id: 4, configurationVersion: "a".repeat(64), name: "Heat water", entityId: "socket-a", sourceInverterId: "primary-inverter", enabled: true,
      socTurnOnThreshold: 80, socTurnOffThreshold: 80, useSeparateSocTurnOffThreshold: false,
      useSolarProductionThreshold: false, minAverageSolarProductionWatts: 3000,
      cooldownMinutes: 15, intervalSeconds: 30, activeFrom: null, activeTo: null,
      currentState: false, currentStateChangedAt: null, lastEvaluated: null
    };
    const calls: { path: string; method: string; body: any }[] = [];
    const api = new DeyeSolarApi(new ApiClient({ baseUrl: "https://solar.example", token: "owner", transport: async (url, init) => {
      const pathname = new URL(url).pathname;
      const body = init.body ? JSON.parse(init.body) : undefined;
      calls.push({ path: pathname, method: init.method, body });
      const result = pathname === "/api/devices" ? { devices: [{ id: "socket-a", name: "Heater", category: null, currentPowerW: null, online: true, stateKnown: true, isOn: false }], lastUpdated: null }
        : pathname === "/api/rules/4" ? init.method === "PUT" ? { ...rule, ...body } : rule
        : pathname === "/api/settings" ? { polling: { intervalSeconds: 30 }, display: { timeZoneId: "UTC" } }
        : pathname === "/api/auth/security/permissions" ? { role: "Owner", permissions: ["Read", "ManageRules"] }
        : pathname === "/api/v2/integration-socket-sources" ? [
          { id: "primary-inverter", name: "Primary inverter", isDefault: defaultAvailable },
          { id: "battery-inverter", name: "Battery inverter", isDefault: false }
        ] : [];
      return { status: 200, ok: true, text: async () => JSON.stringify(result) };
    } }));
    const globals = globalThis as typeof globalThis & { __ruleSourceAuth?: unknown; IS_REACT_ACT_ENVIRONMENT?: boolean };
    globals.__ruleSourceAuth = { api, isDemo: false }; uiGlobals.__smartUi = { auth: { api, isDemo: false } };
    globals.IS_REACT_ACT_ENVIRONMENT = true;
    let renderer: ReturnType<typeof create> | undefined;
    let navigated = 0;
    try {
      const RuleEditor = await component();
      await act(async () => { renderer = create(React.createElement(RuleEditor, { route: { params: { id: 4 } }, navigation: { goBack: () => navigated++ } })); });
      const button = (label: string) => renderer!.root.findAllByType("button").find(item => item.props.label === label)!;
      assert.equal(button("Neighbor socket"), undefined);
      assert.equal(calls.some(call => call.path.includes("disabled-site/devices")), false);
      await act(async () => { renderer!.root.findByProps({ label: "Battery & solar source" }).props.onChange(source ?? "default"); });
      await act(async () => { renderer!.root.findByProps({ label: "Name" }).props.onChangeText("Draft rule name"); });
      assert.equal(renderer!.root.findAllByType("button").some(item => /Refresh|Reload/.test(item.props.label)), false);
      assert.equal(renderer!.root.findByProps({ label: "Name" }).props.value, "Draft rule name");
      await act(async () => { button("Save automation").props.onPress(); });
      const save = calls.find(call => call.method === "PUT")!;
      assert.equal(save.body.sourceInverterId, source);
      assert.equal(save.body.entityId, "socket-a");
      assert.equal(save.body.name, "Draft rule name");
      assert.equal(navigated, 1);
      assert.equal(calls.filter(call => call.method === "PUT").length, 1);
      assert.equal(calls.some(call => call.path.endsWith("/state")), false);
    } finally {
      if (renderer) await act(async () => renderer!.unmount());
      delete globals.__ruleSourceAuth; delete uiGlobals.__smartUi;
      delete globals.IS_REACT_ACT_ENVIRONMENT;
    }
  });
}

test("a missing saved source is retained; unavailable sources prevent enabling but permit a disabled draft", async () => {
  const rule: Rule = {
    id: 4, configurationVersion: "a".repeat(64), name: "Heat water", entityId: "socket-a", sourceInverterId: "retired-inverter", enabled: true,
    socTurnOnThreshold: 80, socTurnOffThreshold: 80, useSeparateSocTurnOffThreshold: false,
    useSolarProductionThreshold: false, minAverageSolarProductionWatts: 3000,
    cooldownMinutes: 15, intervalSeconds: 30, activeFrom: null, activeTo: null,
    currentState: false, currentStateChangedAt: null, lastEvaluated: null
  };
  const writes: any[] = [];
  const api = new DeyeSolarApi(new ApiClient({ baseUrl: "https://solar.example", transport: async (url, init) => {
    const route = new URL(url).pathname;
    if (init.method === "PUT") writes.push(JSON.parse(init.body!));
    const unavailable = route === "/api/v2/integration-socket-sources";
    const result = unavailable ? { message: "Sources temporarily unavailable." }
      : route === "/api/settings" ? { polling: { intervalSeconds: 30 }, display: { timeZoneId: "UTC" } }
      : route === "/api/auth/security/permissions" ? { role: "Owner", permissions: ["Read", "ManageRules"] }
      : route === "/api/devices" ? { devices: [{ id: "socket-a", name: "Heater", category: null, currentPowerW: null, online: true, stateKnown: true, isOn: false }], lastUpdated: null }
      : rule;
    return { status: unavailable ? 503 : 200, ok: !unavailable, text: async () => JSON.stringify(result) };
  } }));
  const globals = globalThis as typeof globalThis & { __ruleSourceAuth?: unknown; IS_REACT_ACT_ENVIRONMENT?: boolean };
  globals.__ruleSourceAuth = { api, isDemo: false }; uiGlobals.__smartUi = { auth: { api, isDemo: false } };
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  let renderer: ReturnType<typeof create> | undefined;
  try {
    const RuleEditor = await component();
    await act(async () => { renderer = create(React.createElement(RuleEditor, { route: { params: { id: 4 } }, navigation: { goBack() {} } })); });
    const button = renderer!.root.findAllByType("button").find(item => item.props.label === "Save automation")!;
    assert.match(JSON.stringify(renderer!.toJSON()), /retired-inverter/);
    await act(async () => { button.props.onPress(); });
    assert.equal(writes.length, 0);
    await act(async () => { renderer!.root.findAllByType("SwitchRow").find(item => item.props.title === "Enable automation")!.props.onValueChange(false); });
    await act(async () => { button.props.onPress(); });
    assert.equal(writes.length, 1);
    assert.equal(writes[0]!.sourceInverterId, "retired-inverter");
    assert.equal(writes[0]!.enabled, false);
  } finally {
    if (renderer) await act(async () => renderer!.unmount());
    delete globals.__ruleSourceAuth; delete uiGlobals.__smartUi;
    delete globals.IS_REACT_ACT_ENVIRONMENT;
  }
});

test("daylight template saves a reviewed fixed schedule with the real solar guard and never switches a socket", async () => {
  const writes: any[] = [];
  const api = new DeyeSolarApi(new ApiClient({ baseUrl: "https://solar.example", transport: async (url, init) => {
    const path = new URL(url).pathname; const body = init.body ? JSON.parse(init.body) : undefined;
    if (init.method === "POST") writes.push({ path, body });
    const result = path === "/api/devices" ? { devices: [{ id: "socket-a", name: "Heater", category: null, currentPowerW: null, online: true, stateKnown: true, isOn: false }], lastUpdated: null }
      : path === "/api/auth/security/permissions" ? { role: "Owner", permissions: ["Read", "ManageRules"] }
      : path === "/api/settings" ? { polling: { intervalSeconds: 30 }, display: { timeZoneId: "UTC" } }
      : path === "/api/rules" ? { ...body, id: 5, configurationVersion: "a".repeat(64), currentState: false, currentStateChangedAt: null, lastEvaluated: null } : [];
    return { status: 200, ok: true, text: async () => JSON.stringify(result) };
  } }));
  uiGlobals.IS_REACT_ACT_ENVIRONMENT = true; uiGlobals.__smartUi = { auth: { api, isDemo: false } }; let renderer: ReturnType<typeof create> | undefined;
  try {
    const RuleEditor = await component(); await act(async () => { renderer = create(React.createElement(RuleEditor, { route: { params: { template: "daylight" } }, navigation: { goBack() {} } })); });
    assert.equal(renderer.root.findByProps({ label: "From" }).props.value, "06:00"); assert.equal(renderer.root.findByProps({ label: "Until" }).props.value, "18:00");
    assert.equal(renderer.root.findAllByType("SwitchRow").find(node => node.props.title === "Require solar")!.props.value, true);
    assert.equal(renderer.root.findByProps({ label: "Minimum solar power" }).props.value, "0.1");
    assert.ok(renderer.root.findAllByType("Text").some(node => node.props.children === "The saved schedule uses fixed times. Review it when daylight hours change."));
    assert.equal(writes.length, 0);
    await act(async () => { renderer!.root.findByProps({ label: "Target device" }).props.onChange("socket-a"); });
    await act(async () => { renderer!.root.findAllByType("button").find(node => node.props.label === "Save automation")!.props.onPress(); });
    assert.equal(writes.length, 1); assert.equal(writes[0].path, "/api/rules"); assert.equal(writes[0].body.enabled, false);
    assert.equal(writes[0].body.useSolarProductionThreshold, true); assert.equal(writes[0].body.minAverageSolarProductionWatts, 100);
    assert.equal(writes[0].body.activeFrom, "06:00"); assert.equal(writes[0].body.activeTo, "18:00");
  } finally { if (renderer) await act(async () => renderer.unmount()); delete uiGlobals.__smartUi; delete uiGlobals.IS_REACT_ACT_ENVIRONMENT; }
});
