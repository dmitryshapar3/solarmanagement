import assert from "node:assert/strict";
import { test } from "node:test";
import React from "react";
import { act, create } from "react-test-renderer";
import type { InverterData } from "../src/core/api/types";
import { energyFlowDirection, energyFlowReadings } from "../src/ui/energy/energyFlowPolicy";
import { globals, uiHarness } from "./support/uiHarness";

const reading: InverterData = {
  inverterId: "fixture", batterySoc: 62, batteryPower: 600, batteryCurrent: 12,
  batteryTemperature: 20, batteryVoltage: 50, solarProduction: 1200, gridConsumption: 200, loadPower: 2000,
  timestamp: "2026-10-09T17:15:00Z", dataSource: "Fixture", solarObservedAt: "2026-10-09T17:15:00Z",
  gridObservedAt: "2026-10-09T17:15:00Z", solarDeviceSn: "fixture", gridDeviceSn: "fixture",
  batterySocValid: true, batteryPowerValid: true, batteryTemperatureValid: true,
  batteryVoltageValid: true, batteryCurrentValid: true, loadPowerValid: true, gridPowerValid: true, solarPowerValid: true
};

test("energy flow preserves measured zero, unknown and signed storage/grid readings", () => {
  assert.deepEqual(energyFlowReadings(null), { solar: null, grid: null, battery: null, load: null, soc: null });
  assert.deepEqual(energyFlowReadings({ ...reading, solarProduction: 0, loadPower: 0, batteryPower: 0, gridConsumption: 0, batterySoc: 0 }), { solar: 0, grid: 0, battery: 0, load: 0, soc: 0 });
  assert.deepEqual(energyFlowReadings({ ...reading, gridConsumption: -200, batteryPower: -100, solarProduction: -1, loadPower: -1 }), { solar: null, grid: -200, battery: -100, load: null, soc: 62 });
  for (const valid of [false, undefined, null]) {
    const data = { ...reading, solarPowerValid: valid, gridPowerValid: valid, batteryPowerValid: valid, loadPowerValid: valid, batterySocValid: valid } as unknown as InverterData;
    assert.deepEqual(energyFlowReadings(data), { solar: null, grid: null, battery: null, load: null, soc: null });
  }
  for (const invalid of [Number.NaN, Number.POSITIVE_INFINITY, Number.NEGATIVE_INFINITY]) {
    assert.deepEqual(energyFlowReadings({ ...reading, solarProduction: invalid, gridConsumption: invalid, batteryPower: invalid, loadPower: invalid, batterySoc: invalid }), { solar: null, grid: null, battery: null, load: null, soc: null });
  }
  for (const invalid of [-1, 101]) assert.equal(energyFlowReadings({ ...reading, batterySoc: invalid }).soc, null);
});

test("energy flow arrows follow inverter signs and never move for unknown or zero", () => {
  for (const kind of ["solar", "grid", "battery", "load"] as const) {
    for (const watts of [null, 0, Number.NaN, Number.POSITIVE_INFINITY]) assert.equal(energyFlowDirection(kind, watts), null);
    assert.equal(energyFlowDirection(kind, 100), kind === "load" ? "out" : "in");
    assert.equal(energyFlowDirection(kind, -100), kind === "solar" || kind === "load" ? null : "out");
  }
});

type Loop = { active: boolean; config: { useNativeDriver: boolean; isInteraction: boolean; duration: number }; start(): void; stop(): void };
type MotionFixture = {
  appListeners: Set<(state: string) => void>; motionListeners: Set<(value: boolean) => void>;
  loops: Loop[]; motionPromise: Promise<boolean>; focus?: () => void; blur?: () => void;
};
const motionGlobals = globals as typeof globals & { __flowMotion?: MotionFixture };
function prepare(motionPromise = Promise.resolve(false)) {
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  const state: MotionFixture = { appListeners: new Set(), motionListeners: new Set(), loops: [], motionPromise };
  motionGlobals.__flowMotion = state;
  return state;
}
async function component() {
  return (await uiHarness('export { EnergyFlow } from "./src/ui/energy/EnergyFlow";', { stubComponents: true, stubs: {
    "react-native": `const state=globalThis.__flowMotion;
      export const View="View",Text="Text",Appearance={setColorScheme(){}};export const useColorScheme=()=>"light";
      export const AppState={currentState:"active",addEventListener:(_event,listener)=>{state.appListeners.add(listener);return {remove:()=>state.appListeners.delete(listener)}}};
      export const AccessibilityInfo={isReduceMotionEnabled:()=>state.motionPromise,addEventListener:(_event,listener)=>{state.motionListeners.add(listener);return {remove:()=>state.motionListeners.delete(listener)}}};
      export const Easing={linear:value=>value};export const Animated={createAnimatedComponent:component=>component,Value:class{setValue(value){this.value=value}stopAnimation(){}interpolate(config){return config}},timing:(value,config)=>({value,config}),loop:timing=>{const loop={config:timing.config,active:false,start(){this.active=true;state.loops.push(this)},stop(){this.active=false}};return loop}};`,
    "@react-navigation/native": `import React from "react";const state=globalThis.__flowMotion;
      export const useFocusEffect=callback=>React.useEffect(()=>{state.focus=()=>{state.blur=callback()};state.focus();return()=>{state.blur?.();state.focus=undefined;state.blur=undefined}},[callback]);`
  } })).EnergyFlow!;
}
async function dispose(renderer?: ReturnType<typeof create>) {
  await act(async () => renderer?.unmount());
  delete motionGlobals.__flowMotion; delete globals.IS_REACT_ACT_ENVIRONMENT;
}

test("native diagram labels Load clearly, exposes actual values and reverses only signed flows", async () => {
  prepare(Promise.resolve(true)); let renderer: ReturnType<typeof create> | undefined;
  try {
    const EnergyFlow = await component();
    await act(async () => { renderer = create(React.createElement(EnergyFlow, { inverter: reading })); });
    const root = renderer!.root;
    const track = (kind: string) => root.findByProps({ testID: `energy-flow-${kind}-track` });
    const value = (kind: string) => root.findByProps({ testID: `energy-flow-${kind}-value` }).props.children;
    const line = (kind: string) => track(kind).findByType("Line").props;
    assert.equal(value("load"), "2.0 kW");
    assert.ok(root.findAllByType("SvgText").some(item => item.props.children === "Load"));
    assert.ok(!root.findAllByType("SvgText").some(item => item.props.children === "Home"));
    assert.match(root.findByType("Svg").props.accessibilityLabel, /Load: 2.0 kW/);
    assert.ok(line("solar").y2 > line("solar").y1);
    assert.ok(line("battery").x2 > line("battery").x1);
    assert.ok(line("grid").x2 < line("grid").x1);
    assert.ok(line("load").y2 > line("load").y1);
    assert.equal(root.findAllByType("View").filter(item => item.props.style?.height === 7).length, 0);
    await act(async () => { renderer!.update(React.createElement(EnergyFlow, { inverter: { ...reading, batteryPower: -600, gridConsumption: -200 } })); });
    assert.ok(line("battery").x2 < line("battery").x1);
    assert.ok(line("grid").x2 > line("grid").x1);
    assert.ok(root.findAllByType("Text").some(item => item.props.children === "Charging"));
    assert.ok(root.findAllByType("Text").some(item => item.props.children === "Exporting"));
    await act(async () => { renderer!.update(React.createElement(EnergyFlow, { inverter: { ...reading, solarProduction: -1, loadPower: -1, batteryPower: 0, gridPowerValid: false } })); });
    assert.equal(value("load"), "—"); assert.equal(value("solar"), "—");
    assert.equal(value("battery"), "0 W"); assert.equal(value("grid"), "—");
    for (const kind of ["solar", "load", "battery", "grid"]) assert.equal(track(kind).findAllByType("Path").length, 0);
  } finally { await dispose(renderer); }
});

test("native flow animation pauses on background, screen blur, reduce motion, all-zero data and unmount", async () => {
  const state = prepare(); let renderer: ReturnType<typeof create> | undefined;
  try {
    const EnergyFlow = await component();
    await act(async () => { renderer = create(React.createElement(EnergyFlow, { inverter: reading })); });
    const active = () => state.loops.filter(loop => loop.active).length;
    assert.equal(active(), 1);
    assert.equal(state.loops[0]!.config.duration, 1600);
    assert.equal(state.loops[0]!.config.useNativeDriver, false);
    assert.equal(state.loops[0]!.config.isInteraction, false);
    const arrow = renderer!.root.findByProps({ testID: "energy-flow-grid-moving" });
    assert.deepEqual(arrow.props.transform[0].translateX.outputRange, [14, 68]);
    await act(async () => { renderer!.update(React.createElement(EnergyFlow, { inverter: { ...reading, loadPower: 2100 } })); });
    assert.equal(state.loops.length, 1, "a new power reading does not restart motion");
    await act(async () => { for (const listener of state.appListeners) listener("inactive"); });
    assert.equal(active(), 0);
    assert.ok(renderer!.root.findByProps({ testID: "energy-flow-grid-static" }));
    await act(async () => { for (const listener of state.appListeners) listener("active"); }); assert.equal(active(), 1);
    await act(async () => { state.blur?.(); state.blur = undefined; }); assert.equal(active(), 0);
    await act(async () => { state.focus?.(); }); assert.equal(active(), 1);
    await act(async () => { for (const listener of state.motionListeners) listener(true); }); assert.equal(active(), 0);
    await act(async () => { for (const listener of state.motionListeners) listener(false); }); assert.equal(active(), 1);
    await act(async () => { renderer!.update(React.createElement(EnergyFlow, { inverter: { ...reading, solarProduction: 0, gridConsumption: 0, batteryPower: 0, loadPower: 0 } })); });
    assert.equal(active(), 0);
    await act(async () => { renderer!.update(React.createElement(EnergyFlow, { inverter: reading })); }); assert.equal(active(), 1);
    await act(async () => { renderer!.unmount(); }); renderer = undefined;
    assert.equal(active(), 0); assert.equal(state.appListeners.size, 0); assert.equal(state.motionListeners.size, 0);
  } finally { await dispose(renderer); }
});

test("a delayed initial accessibility result cannot overwrite a newer reduce-motion preference", async () => {
  let resolveMotion!: (value: boolean) => void;
  const state = prepare(new Promise<boolean>(resolve => { resolveMotion = resolve; }));
  let renderer: ReturnType<typeof create> | undefined;
  try {
    const EnergyFlow = await component();
    await act(async () => { renderer = create(React.createElement(EnergyFlow, { inverter: reading })); });
    assert.equal(state.loops.length, 0);
    await act(async () => { for (const listener of state.motionListeners) listener(true); });
    await act(async () => { resolveMotion(false); });
    assert.equal(state.loops.length, 0);
    assert.ok(renderer!.root.findByProps({ testID: "energy-flow-grid-static" }));
  } finally { await dispose(renderer); }
});
