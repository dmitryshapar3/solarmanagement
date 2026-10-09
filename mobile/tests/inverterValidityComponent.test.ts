import assert from "node:assert/strict";
import { test } from "node:test";
import { createRequire } from "node:module";
import path from "node:path";
import React from "react";
import { act, create } from "react-test-renderer";
import { build } from "esbuild";
import type { InverterData } from "../src/core/api/types";

function inverterFixture(): InverterData {
  const timestamp = new Date().toISOString();
  return { inverterId: "fixture-inverter", solarObservedAt: timestamp, gridObservedAt: timestamp,
    solarDeviceSn: "fixture-inverter", gridDeviceSn: "fixture-inverter", timestamp, dataSource: "Fixture inverter",
    batterySoc: 0, batteryPower: 0, batteryCurrent: 0, batteryTemperature: 0, batteryVoltage: 0,
    loadPower: 0, solarProduction: 0, gridConsumption: 0, batterySocValid: false, batteryPowerValid: false,
    batteryTemperatureValid: false, batteryVoltageValid: false, batteryCurrentValid: false,
    loadPowerValid: false, gridPowerValid: false, solarPowerValid: false };
}

import { globals as uiGlobals, uiHarness } from "./support/uiHarness";
async function components() {
  const resources: Record<string, any> = {};
  for (const key of ["inverter-details", "home-dashboard"]) Object.defineProperty(resources, key, { get: () => ({ data: { inverter: (globalThis as any).__inverterValidityReading, timeZoneId: "UTC", manualDevices: [], devices: [], rules: [] }, loading: false, error: null, refresh: async () => {}, invalidate() {} }) });
  uiGlobals.__smartUi = { auth: { api: {}, isDemo: true }, resources };
  return await uiHarness('export { LiveReadingsScreen as InverterDetailsScreen } from "./src/features/readings/LiveReadingsScreen"; export { DashboardScreen } from "./src/features/dashboard/DashboardScreen"; export { SolarComparison as CurrentSolarSnapshot } from "./src/features/generation/ProductionView";', { stubComponents: true, resources: true, stubs: { ProductionChart: 'export const ProductionChart=()=>null;' } });
}

test("the actual inverter details hide missing power values while retaining only explicitly valid zeros", async () => {
  const globals = globalThis as typeof globalThis & { __inverterValidityReading?: InverterData; IS_REACT_ACT_ENVIRONMENT?: boolean };
  const reading: InverterData = { ...inverterFixture(), batterySoc: 0, batteryPower: 0, batteryCurrent: 0, batteryTemperature: 0,
    batteryVoltage: 0, loadPower: 0, solarProduction: 4100, gridConsumption: 0, timestamp: new Date().toISOString(),
    dataSource: "Fixture inverter", batterySocValid: false, batteryPowerValid: false, batteryTemperatureValid: false,
    batteryVoltageValid: false, batteryCurrentValid: false, loadPowerValid: false, gridPowerValid: false, solarPowerValid: true };
  globals.__inverterValidityReading = reading;
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  let renderer: ReturnType<typeof create> | undefined;
  try {
    const Component = (await components()).InverterDetailsScreen;
    await act(async () => { renderer = create(React.createElement(Component)); });
    const metric = (label: string) => renderer!.root.findAllByType("View").find(item => {
      const text = item.findAllByType("Text");
      return text[0]?.props.children === label && text.length <= 3 && text.length >= 2;
    })!;
    const value = (label: string) => metric(label).findAllByType("Text").at(-1)!.props.children;
    for (const label of ["State of charge", "Power", "Load", "Grid power"]) assert.equal(value(label), "-", label);
    for (const label of ["Voltage", "Current", "Temperature", "Battery power", "Balance difference"]) assert.equal(value(label), "—", label);
    assert.equal(value("Solar generation"), "4.1 kW");
    assert.equal(renderer!.root.findAllByProps({ accessibilityRole: "progressbar" }).length, 0);
    assert.ok(metric("Grid power").findAllByType("Text").some(item => item.props.children === "Unavailable"));
    globals.__inverterValidityReading = { ...reading, solarProduction: 0, solarPowerValid: false, gridPowerValid: true };
    await act(async () => { renderer!.update(React.createElement(Component)); });
    assert.equal(value("Solar generation"), "-");
    assert.equal(value("Grid power"), "0 W");
    for (const flag of [true]) {
      globals.__inverterValidityReading = { ...reading, solarProduction: 0, batterySocValid: flag, batteryPowerValid: flag, batteryTemperatureValid: flag,
        batteryVoltageValid: flag, batteryCurrentValid: flag, loadPowerValid: flag, gridPowerValid: flag, solarPowerValid: flag };
      await act(async () => { renderer!.update(React.createElement(Component)); });
      assert.equal(value("State of charge"), "0%");
      for (const label of ["Power", "Load", "Battery idle"]) assert.equal(value(label), "0 W", label);
      assert.equal(value("Voltage"), "0 V");
      assert.equal(value("Current"), "0 A");
      assert.equal(value("Temperature"), "0 °C");
      assert.equal(renderer!.root.findAllByProps({ accessibilityRole: "progressbar" }).length, 1);
      assert.equal(renderer!.root.findByProps({ accessibilityRole: "progressbar" }).props.accessibilityValue.now, 0);
      assert.equal(value("Grid power"), "0 W");
      assert.ok(metric("Grid power").findAllByType("Text").some(item => item.props.children === "Idle"));
      assert.equal(value("Solar generation"), "0 W");
      assert.equal(value("Balance difference"), "0 W");
    }
    for (const flag of [null, undefined]) {
      globals.__inverterValidityReading = { ...reading, solarProduction: 0, batterySocValid: flag, batteryPowerValid: flag,
        batteryTemperatureValid: flag, batteryVoltageValid: flag, batteryCurrentValid: flag,
        loadPowerValid: flag, gridPowerValid: flag, solarPowerValid: flag } as unknown as InverterData;
      await act(async () => { renderer!.update(React.createElement(Component)); });
      for (const label of ["State of charge", "Power", "Load", "Grid power", "Solar generation"]) assert.equal(value(label), "-", label);
      assert.equal(value("Balance difference"), "—");
      assert.equal(renderer!.root.findAllByProps({ accessibilityRole: "progressbar" }).length, 0);
    }

  } finally {
    if (renderer) await act(async () => renderer!.unmount());
    delete globals.__inverterValidityReading; delete uiGlobals.__smartUi;
    delete globals.IS_REACT_ACT_ENVIRONMENT;
  }
});

test("the actual dashboard shows missing grid and solar as unavailable and preserves explicitly confirmed zero", async () => {
  const globals = globalThis as typeof globalThis & { __inverterValidityReading?: InverterData; IS_REACT_ACT_ENVIRONMENT?: boolean };
  const reading: InverterData = { ...inverterFixture(), batterySoc: 0, batteryPower: 0, batteryCurrent: 0, batteryTemperature: 0,
    batteryVoltage: 0, loadPower: 0, solarProduction: 0, gridConsumption: 0, timestamp: new Date().toISOString(),
    dataSource: "Fixture inverter", gridPowerValid: false, solarPowerValid: false };
  globals.__inverterValidityReading = reading;
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  let renderer: ReturnType<typeof create> | undefined;
  try {
    const Component = (await components()).DashboardScreen;
    await act(async () => { renderer = create(React.createElement(Component)); });
    const solar = () => renderer!.root.findAllByType("Text").find(item => item.props.style?.fontSize === 72)!.props.children;
    const grid = () => renderer!.root.findByProps({ testID: "energy-flow-grid-value" }).props.children;
    assert.equal(solar(), "—"); assert.equal(grid(), "—");
    globals.__inverterValidityReading = { ...reading, gridPowerValid: true, solarPowerValid: true };
    await act(async () => { renderer!.update(React.createElement(Component)); });
    assert.equal(solar(), "0.00"); assert.equal(grid(), "0 W");
    for (const flag of [null, undefined]) {
      globals.__inverterValidityReading = { ...reading, gridPowerValid: flag, solarPowerValid: flag } as unknown as InverterData;
      await act(async () => { renderer!.update(React.createElement(Component)); });
      assert.equal(solar(), "—"); assert.equal(grid(), "—");
    }

  } finally {
    if (renderer) await act(async () => renderer!.unmount());
    delete globals.__inverterValidityReading; delete uiGlobals.__smartUi;
    delete globals.IS_REACT_ACT_ENVIRONMENT;
  }
});

test("the generation snapshot preserves the distinction between missing PV and explicitly confirmed zero", async () => {
  const Component = (await components()).CurrentSolarSnapshot;
  const reading: InverterData = { ...inverterFixture(), batterySoc: 0, batteryPower: 0, batteryCurrent: 0, batteryTemperature: 0,
    batteryVoltage: 0, loadPower: 0, solarProduction: 0, gridConsumption: 0, timestamp: new Date().toISOString(), dataSource: "Fixture inverter" };
  const globals = globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT?: boolean };
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  let renderer: ReturnType<typeof create> | undefined;
  try {
    const props = { state: null, inverter: { ...reading, solarPowerValid: false }, timeZone: "UTC" };
    await act(async () => { renderer = create(React.createElement(Component, props)); });
    const value = () => renderer!.root.findAllByType("View").find(item => {
      const text = item.findAllByType("Text");
      return text.length === 3 && text[0]?.props.children === "Inverter";
    })!.findAllByType("Text")[1]!.props.children;
    assert.equal(value(), "— kW");
    for (const flag of [true]) {
      await act(async () => { renderer!.update(React.createElement(Component, { ...props, inverter: { ...reading, solarPowerValid: flag } })); });
      assert.equal(value(), "0.00 kW");
    }
    for (const flag of [null, undefined]) {
      await act(async () => { renderer!.update(React.createElement(Component, { ...props, inverter: { ...reading, solarPowerValid: flag } })); });
      assert.equal(value(), "— kW");
    }

  } finally {
    if (renderer) await act(async () => renderer!.unmount());
    delete globals.IS_REACT_ACT_ENVIRONMENT;
  }
});
