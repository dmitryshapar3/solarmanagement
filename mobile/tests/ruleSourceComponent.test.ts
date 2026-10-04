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

async function component() {
  const bundle = await build({
    stdin: { contents: 'export { RuleEditorScreen } from "./src/features/rules/RuleEditorScreen";', resolveDir: process.cwd(), loader: "ts" },
    bundle: true, write: false, platform: "node", format: "cjs", external: ["react", "react/jsx-runtime"],
    plugins: [{ name: "rule-platform-boundaries", setup(builder) {
      builder.onResolve({ filter: /^(react-native|lucide-react-native)$/ }, args => ({ path: args.path, namespace: "native-test" }));
      builder.onResolve({ filter: /(?:^|\/)application\/LanguageContext$/ }, () => ({ path: "language", namespace: "native-test" }));
      builder.onResolve({ filter: /(?:^|\/)core\/components$/ }, () => ({ path: "components", namespace: "native-test" }));
      builder.onResolve({ filter: /(?:^|\/)application\/AuthContext$/ }, () => ({ path: "auth", namespace: "native-test" }));
      builder.onLoad({ filter: /.*/, namespace: "native-test" }, args => ({
        loader: "js", contents: args.path === "language" ? 'export const useLanguage = () => ({language:"en", t:(phrase,...args) => (phrase ?? "").replace(/\\{(\\d+)\\}/g, (token, index) => args[Number(index)] === undefined ? token : String(args[Number(index)] ?? ""))});' : args.path === "react-native"
          ? 'export const Keyboard = {dismiss(){}}; export const StyleSheet = {create: value => value}; export const Text = "Text"; export const View = "View"; export const Pressable = "Pressable";'
          : args.path === "lucide-react-native" ? 'export const Save = () => null;'
          : args.path === "auth" ? 'export const useAuth = () => globalThis.__ruleSourceAuth;'
          : 'import React from "react"; export const AppButton = props => React.createElement("button", props, props.label); export const Card = "Card"; export const SectionTitle = "SectionTitle"; export const ErrorBanner = "ErrorBanner"; export const TextField = "TextField"; export const EmptyState = "EmptyState"; export const StatusPill = "StatusPill"; export const SwitchRow = "SwitchRow"; export const Header = "Header"; export const LoadingState = "LoadingState"; export const Screen = "Screen";'
      }));
    } }]
  });
  const module = { exports: {} as { RuleEditorScreen: React.ComponentType<any> } };
  new Function("require", "module", "exports", bundle.outputFiles[0]!.text)(createRequire(path.join(process.cwd(), "package.json")), module, module.exports);
  return module.exports.RuleEditorScreen;
}

for (const { source, defaultAvailable } of [{ source: "battery-inverter", defaultAvailable: true },
  { source: null, defaultAvailable: true }, { source: null, defaultAvailable: false }]) {
  test(`the real rule editor saves source ${source ?? (defaultAvailable ? "installation default" : "socket link without a default")} without changing its socket`, async () => {
    const rule: Rule = {
      id: 4, name: "Heat water", entityId: "socket-a", sourceInverterId: "primary-inverter", enabled: true,
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
      const result = pathname === "/api/devices" ? { devices: [{ id: "socket-a", name: "Heater", online: true, isOn: false }], lastUpdated: null }
        : pathname === "/api/rules/4" ? init.method === "PUT" ? { ...rule, ...body } : rule
        : pathname === "/api/v2/integration-socket-sources" ? [
          { id: "primary-inverter", name: "Primary inverter", isDefault: defaultAvailable },
          { id: "battery-inverter", name: "Battery inverter", isDefault: false }
        ] : [];
      return { status: 200, ok: true, text: async () => JSON.stringify(result) };
    } }));
    const globals = globalThis as typeof globalThis & { __ruleSourceAuth?: unknown; IS_REACT_ACT_ENVIRONMENT?: boolean };
    globals.__ruleSourceAuth = { api, isDemo: false };
    globals.IS_REACT_ACT_ENVIRONMENT = true;
    let renderer: ReturnType<typeof create> | undefined;
    let navigated = 0;
    try {
      const RuleEditor = await component();
      await act(async () => { renderer = create(React.createElement(RuleEditor, { route: { params: { id: 4 } }, navigation: { goBack: () => navigated++ } })); });
      const button = (label: string) => renderer!.root.findAllByType("button").find(item => item.props.label === label)!;
      assert.equal(button("Neighbor socket"), undefined);
      assert.equal(calls.some(call => call.path.includes("disabled-site/devices")), false);
      await act(async () => { button(source === null ? "Socket-linked or installation default inverter" : "Battery inverter").props.onPress(); });
      await act(async () => { renderer!.root.findByProps({ label: "Rule name" }).props.onChangeText("Draft rule name"); });
      await act(async () => { button("Reload inverter sources").props.onPress(); });
      assert.equal(renderer!.root.findByProps({ label: "Rule name" }).props.value, "Draft rule name");
      await act(async () => { button("Save").props.onPress(); });
      const save = calls.find(call => call.method === "PUT")!;
      assert.equal(save.body.sourceInverterId, source);
      assert.equal(save.body.entityId, "socket-a");
      assert.equal(save.body.name, "Draft rule name");
      assert.equal(navigated, 1);
      assert.equal(calls.filter(call => call.method === "PUT").length, 1);
      assert.equal(calls.some(call => call.path.endsWith("/state")), false);
    } finally {
      if (renderer) await act(async () => renderer!.unmount());
      delete globals.__ruleSourceAuth;
      delete globals.IS_REACT_ACT_ENVIRONMENT;
    }
  });
}

test("a missing saved source is retained; unavailable sources prevent enabling but permit a disabled draft", async () => {
  const rule: Rule = {
    id: 4, name: "Heat water", entityId: "socket-a", sourceInverterId: "retired-inverter", enabled: true,
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
      : route === "/api/devices" ? { devices: [{ id: "socket-a", name: "Heater", online: true, isOn: false }], lastUpdated: null }
      : rule;
    return { status: unavailable ? 503 : 200, ok: !unavailable, text: async () => JSON.stringify(result) };
  } }));
  const globals = globalThis as typeof globalThis & { __ruleSourceAuth?: unknown; IS_REACT_ACT_ENVIRONMENT?: boolean };
  globals.__ruleSourceAuth = { api, isDemo: false };
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  let renderer: ReturnType<typeof create> | undefined;
  try {
    const RuleEditor = await component();
    await act(async () => { renderer = create(React.createElement(RuleEditor, { route: { params: { id: 4 } }, navigation: { goBack() {} } })); });
    const button = renderer!.root.findAllByType("button").find(item => item.props.label === "Save")!;
    assert.match(JSON.stringify(renderer!.toJSON()), /retired-inverter/);
    await act(async () => { button.props.onPress(); });
    assert.equal(writes.length, 0);
    await act(async () => { renderer!.root.findAllByType("SwitchRow").find(item => item.props.title === "Enabled")!.props.onValueChange(false); });
    await act(async () => { button.props.onPress(); });
    assert.equal(writes.length, 1);
    assert.equal(writes[0]!.sourceInverterId, "retired-inverter");
    assert.equal(writes[0]!.enabled, false);
  } finally {
    if (renderer) await act(async () => renderer!.unmount());
    delete globals.__ruleSourceAuth;
    delete globals.IS_REACT_ACT_ENVIRONMENT;
  }
});
