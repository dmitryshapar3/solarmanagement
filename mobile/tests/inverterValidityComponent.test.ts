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

async function components() {
  const bundle = await build({
    stdin: { contents: 'export { InverterDetailsScreen } from "./src/features/dashboard/InverterDetailsScreen"; export { DashboardScreen } from "./src/features/dashboard/DashboardScreen"; export { CurrentSolarSnapshot } from "./src/features/generation/GenerationScreen";', resolveDir: process.cwd(), loader: "ts" },
    bundle: true, write: false, platform: "node", format: "cjs", external: ["react", "react/jsx-runtime", "expo-crypto"],
    plugins: [{ name: "inverter-display-boundaries", setup(builder) {
      builder.onResolve({ filter: /^(react-native|lucide-react-native|@react-navigation\/native)$/ }, args => ({ path: args.path, namespace: "native-test" }));
      builder.onResolve({ filter: /(?:^|\/)(GenerationScreen|SalesScreen|TileHeader)$/ }, args => args.path.endsWith("GenerationScreen") && !args.importer.endsWith("DashboardScreen.tsx")
        ? undefined : ({ path: "panels", namespace: "native-test" }));
      builder.onResolve({ filter: /(?:^|\/)(EnergyChart|EnergyControls)$/ }, () => ({ path: "panels", namespace: "native-test" }));
      builder.onResolve({ filter: /(?:^|\/)i18n$/ }, () => ({ path: "i18n", namespace: "native-test" }));
      builder.onResolve({ filter: /(?:^|\/)application\/LanguageContext$/ }, () => ({ path: "language", namespace: "native-test" }));
      builder.onResolve({ filter: /(?:^|\/)core\/components$/ }, () => ({ path: "components", namespace: "native-test" }));
      builder.onResolve({ filter: /(?:^|\/)application\/AuthContext$/ }, () => ({ path: "auth", namespace: "native-test" }));
      builder.onResolve({ filter: /(?:^|\/)useFocusedResource$/ }, () => ({ path: "resource", namespace: "native-test" }));
      builder.onLoad({ filter: /.*/, namespace: "native-test" }, args => ({ loader: "js", contents: args.path === "i18n" ? 'export const currentLocale = () => "en"; export const formattingLocale = () => "en-GB"; export const translate = (phrase,...args) => (phrase ?? "").replace(/\\{(\\d+)\\}/g, (token,index) => args[Number(index)] === undefined ? token : String(args[Number(index)] ?? ""));' : args.path === "language" ? 'export const useLanguage = () => ({language:"en", t:(phrase,...args) => (phrase ?? "").replace(/\\{(\\d+)\\}/g, (token, index) => args[Number(index)] === undefined ? token : String(args[Number(index)] ?? ""))});' : args.path === "react-native"
        ? 'export const StyleSheet = {create: value => value}; export const Text = "Text"; export const View = "View"; export const Pressable = "Pressable"; export const ScrollView = "ScrollView"; export const Linking = {};'
        : args.path === "lucide-react-native" ? 'export const RefreshCcw = () => null; export const CirclePower = () => null; export const PlugZap = () => null;'
        : args.path === "@react-navigation/native" ? 'import React from "react"; export const useFocusEffect = callback => React.useEffect(callback,[callback]); export const useNavigation = () => ({navigate(){}});'
        : args.path === "panels" ? 'export const GenerationPanel = () => null; export const SalesPanel = () => null; export const TileHeader = () => null; export const EnergyChart = () => null; export const PeriodNavigation = () => null; export const energyStyles = {};'
        : args.path === "auth" ? 'const api = {socketCommands:{sessionEpoch:0,subscribe:()=>()=>{},get:()=>null,isRunning:()=>false}}; export const useAuth = () => ({api,isDemo:false});'
        : args.path === "resource" ? 'export const useFocusedResource = () => ({data:{inverter:globalThis.__inverterValidityReading,timeZoneId:"UTC",manualDevices:[],devices:[],rules:[]},loading:false,error:null,refresh:async()=>{}});'
        : 'import React from "react"; export const AppButton = props => React.createElement("button", props, props.label); export const Card = "Card"; export const SectionTitle = "SectionTitle"; export const ErrorBanner = "ErrorBanner"; export const EmptyState = "EmptyState"; export const Header = "Header"; export const LoadingState = "LoadingState"; export const ProgressBar = "ProgressBar"; export const Screen = "Screen"; export const StatusPill = "StatusPill"; export const SegmentedControl = "SegmentedControl";'
      }));
    } }]
  });
  const module = { exports: {} as { InverterDetailsScreen: React.ComponentType; DashboardScreen: React.ComponentType; CurrentSolarSnapshot: React.ComponentType<any> } };
  new Function("require", "module", "exports", bundle.outputFiles[0]!.text)(createRequire(path.join(process.cwd(), "package.json")), module, module.exports);
  return module.exports;
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
    assert.equal(renderer!.root.findAllByType("ProgressBar").length, 0);
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
      assert.equal(renderer!.root.findAllByType("ProgressBar").length, 1);
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
      assert.equal(renderer!.root.findAllByType("ProgressBar").length, 0);
    }

  } finally {
    if (renderer) await act(async () => renderer!.unmount());
    delete globals.__inverterValidityReading;
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
    const metric = (label: string) => renderer!.root.findAllByType("View").find(item => {
      const text = item.findAllByType("Text");
      return text[0]?.props.children === label && text.length === 3;
    })!;
    const metricText = (label: string) => metric(label).findAllByType("Text").map(item => item.props.children);
    assert.deepEqual(metricText("Grid"), ["Grid", "—", "Awaiting reading"]);
    assert.deepEqual(metricText("Solar power"), ["Solar power", "—", "Awaiting reading"]);
    for (const flag of [true]) {
      globals.__inverterValidityReading = { ...reading, gridPowerValid: flag, solarPowerValid: flag };
      await act(async () => { renderer!.update(React.createElement(Component)); });
      assert.deepEqual(metricText("Grid"), ["Grid", "0 W", "Idle"]);
      assert.deepEqual(metricText("Solar power"), ["Solar power", "0 W", "Latest inverter reading"]);
    }
    for (const flag of [null, undefined]) {
      globals.__inverterValidityReading = { ...reading, gridPowerValid: flag, solarPowerValid: flag } as unknown as InverterData;
      await act(async () => { renderer!.update(React.createElement(Component)); });
      assert.deepEqual(metricText("Grid"), ["Grid", "—", "Awaiting reading"]);
      assert.deepEqual(metricText("Solar power"), ["Solar power", "—", "Awaiting reading"]);
    }

  } finally {
    if (renderer) await act(async () => renderer!.unmount());
    delete globals.__inverterValidityReading;
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
    const props = { state: null, liveInverter: { ...reading, solarPowerValid: false }, timeZoneId: "UTC", onRefresh() {} };
    await act(async () => { renderer = create(React.createElement(Component, props)); });
    const value = () => renderer!.root.findAllByType("View").find(item => {
      const text = item.findAllByType("Text");
      return text.length === 2 && text[0]?.props.children === "Latest reported inverter";
    })!.findAllByType("Text")[1]!.props.children;
    assert.equal(value(), "— kW");
    for (const flag of [true]) {
      await act(async () => { renderer!.update(React.createElement(Component, { ...props, liveInverter: { ...reading, solarPowerValid: flag } })); });
      assert.equal(value(), "0.00 kW");
    }
    for (const flag of [null, undefined]) {
      await act(async () => { renderer!.update(React.createElement(Component, { ...props, liveInverter: { ...reading, solarPowerValid: flag } })); });
      assert.equal(value(), "— kW");
    }

  } finally {
    if (renderer) await act(async () => renderer!.unmount());
    delete globals.IS_REACT_ACT_ENVIRONMENT;
  }
});
