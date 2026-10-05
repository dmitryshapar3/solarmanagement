import assert from "node:assert/strict";
import { test } from "node:test";
import { createRequire } from "node:module";
import path from "node:path";
import React from "react";
import { act, create, type ReactTestInstance } from "react-test-renderer";
import { build } from "esbuild";
import type { Reading, RuleRunLog } from "../src/core/api/types";
import { demoReadings } from "../src/features/demo/fixtures";

async function historyComponent() {
  const bundle = await build({
    stdin: { contents: 'export { HistoryScreen } from "./src/features/history/HistoryScreen";', resolveDir: process.cwd(), loader: "ts" },
    bundle: true, write: false, platform: "node", format: "cjs", external: ["react", "react/jsx-runtime"],
    plugins: [{ name: "history-boundaries", setup(builder) {
      builder.onResolve({ filter: /^(react-native|lucide-react-native|@react-navigation\/native)$|(?:^|\/)application\/(AuthContext|LanguageContext)$|(?:^|\/)core\/components$|(?:^|\/)i18n$/ }, args => ({ path: args.path, namespace: "history-test" }));
      builder.onLoad({ filter: /.*/, namespace: "history-test" }, args => ({ loader: "js", contents:
        args.path === "react-native" ? 'import React from "react"; export const AppState={currentState:"active",addEventListener:()=>({remove(){}})}; export const StyleSheet={create:value=>value,hairlineWidth:1}; export const Text="Text", View="View", RefreshControl="RefreshControl"; export const FlatList=props=>React.createElement("FlatList",props,props.ListHeaderComponent,...props.data.map(item=>React.createElement(React.Fragment,{key:item.id},props.renderItem({item}))),props.data.length?null:props.ListEmptyComponent);'
          : args.path === "@react-navigation/native" ? 'import React from "react"; export const useFocusEffect=callback=>React.useEffect(callback,[callback]);'
          : args.path === "lucide-react-native" ? 'export const RefreshCcw=()=>null;'
          : args.path.endsWith("AuthContext") ? 'const api={sessionEpoch:0,onSessionChange:()=>()=>{},getRuleRuns:async()=>globalThis.__solarHistory.runs,getReadings:async()=>globalThis.__solarHistory.readings}; export const useAuth=()=>({api,isDemo:false});'
          : args.path.endsWith("LanguageContext") ? 'export const useLanguage=()=>({t:text=>text});'
          : args.path.endsWith("i18n") ? 'export const formattingLocale=()=>"en-GB"; export const translate=text=>text;'
          : 'export const AppButton="AppButton",Card="Card",EmptyState="EmptyState",ErrorBanner="ErrorBanner",Header="Header",LoadingState="LoadingState",Screen="Screen",SegmentedControl="SegmentedControl",StatusPill="StatusPill";'
      }));
    } }]
  });
  const module = { exports: {} as { HistoryScreen: React.ComponentType } };
  new Function("require", "module", "exports", bundle.outputFiles[0]!.text)(createRequire(path.join(process.cwd(), "package.json")), module, module.exports);
  return module.exports.HistoryScreen;
}

function metric(card: ReactTestInstance, label: string) {
  const point = card.findAllByType("View").find(view => {
    const text = view.findAllByType("Text");
    return text.length === 2 && text[0]?.props.children === label;
  });
  assert.ok(point, `metric ${label}`);
  return point.findAllByType("Text")[1]!.props.children;
}

test("actual history keeps absent rule-run telemetry separate from measured zero without interpreting reason text", async () => {
  const base: RuleRunLog = { id: 1, timestamp: "2026-10-05T12:00:00Z", ruleName: "Unknown reading", action: "NO_CHANGE", conditionKey: "soc",
    reason: "All measurements are zero", batterySoc: null, solarProduction: null, batteryPower: null };
  const globals = globalThis as typeof globalThis & { __solarHistory?: { runs: RuleRunLog[]; readings: Reading[] }; IS_REACT_ACT_ENVIRONMENT?: boolean };
  globals.__solarHistory = { runs: [base, { ...base, id: 2, ruleName: "Measured zero", reason: "SOC unavailable", batterySoc: 0, solarProduction: 0, batteryPower: 0 }], readings: [] };
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  let renderer: ReturnType<typeof create> | undefined;
  try {
    const Component = await historyComponent();
    await act(async () => { renderer = create(React.createElement(Component)); });
    const cards = renderer!.root.findAllByType("Card");
    assert.equal(cards.length, 2);
    for (const label of ["SOC", "Solar", "Battery"]) assert.equal(metric(cards[0]!, label), "—", label);
    assert.equal(metric(cards[1]!, "SOC"), "0%");
    assert.equal(metric(cards[1]!, "Solar"), "0 W");
    assert.equal(metric(cards[1]!, "Battery"), "0 W");
  } finally {
    await act(async () => { renderer?.unmount(); });
    delete globals.__solarHistory; delete globals.IS_REACT_ACT_ENVIRONMENT;
  }
});

test("actual reading history masks each invalid measurement and retains explicitly valid zero", async () => {
  const base: Reading = { ...demoReadings(1, new Date("2026-10-05T12:00:00Z"), "UTC")[0]!, batterySoc: 0, solarProduction: 0,
    batteryPower: 0, gridConsumption: 0, loadPower: 0, batteryVoltage: 0, batteryTemperature: 0, batteryCurrent: 0 };
  const unknown: Reading = { ...base, id: 1, batterySocValid: false, solarPowerValid: false, batteryPowerValid: false, gridPowerValid: false,
    loadPowerValid: false, batteryVoltageValid: false, batteryTemperatureValid: false, batteryCurrentValid: false };
  const globals = globalThis as typeof globalThis & { __solarHistory?: { runs: RuleRunLog[]; readings: Reading[] }; IS_REACT_ACT_ENVIRONMENT?: boolean };
  globals.__solarHistory = { runs: [], readings: [unknown, { ...base, id: 2 }] };
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  let renderer: ReturnType<typeof create> | undefined;
  try {
    const Component = await historyComponent();
    await act(async () => { renderer = create(React.createElement(Component)); });
    const mode = renderer!.root.findAllByType("SegmentedControl").find(control => control.props.value === "runs")!;
    await act(async () => { mode.props.onChange("readings"); });
    const cards = renderer!.root.findAllByType("Card");
    assert.equal(cards.length, 2);
    for (const label of ["SOC", "Solar", "Battery", "Grid", "Load", "Voltage", "Temp"])
      assert.equal(metric(cards[0]!, label), "—", label);
    assert.equal(metric(cards[1]!, "SOC"), "0%");
    for (const label of ["Solar", "Battery", "Grid", "Load"]) assert.equal(metric(cards[1]!, label), "0 W", label);
    assert.equal(metric(cards[1]!, "Voltage"), "0.0 V");
    assert.equal(metric(cards[1]!, "Temp"), "0.0 C");
  } finally {
    await act(async () => { renderer?.unmount(); });
    delete globals.__solarHistory; delete globals.IS_REACT_ACT_ENVIRONMENT;
  }
});
