import assert from "node:assert/strict";
import { test } from "node:test";
import { createRequire } from "node:module";
import path from "node:path";
import React from "react";
import { act, create } from "react-test-renderer";
import { build } from "esbuild";
import { ApiClient } from "../src/core/api/ApiClient";
import { IntegrationApi, type SocketCommandReceipt } from "../src/core/api/IntegrationApi";
import { SocketCommandCoordinator } from "../src/core/api/SocketCommandCoordinator";

async function component() {
  const bundle = await build({
    stdin: { contents: 'export { DevicesScreen } from "./src/features/devices/DevicesScreen";', resolveDir: process.cwd(), loader: "ts" },
    bundle: true, write: false, platform: "node", format: "cjs", external: ["react", "react/jsx-runtime", "expo-crypto"],
    plugins: [{ name: "device-platform-boundaries", setup(builder) {
      builder.onResolve({ filter: /^(react-native|lucide-react-native|@react-navigation\/native)$/ }, args => ({ path: args.path, namespace: "native-test" }));
      builder.onResolve({ filter: /(?:^|\/)application\/LanguageContext$/ }, () => ({ path: "language", namespace: "native-test" }));
      builder.onResolve({ filter: /(?:^|\/)core\/components$/ }, () => ({ path: "components", namespace: "native-test" }));
      builder.onResolve({ filter: /(?:^|\/)application\/AuthContext$/ }, () => ({ path: "auth", namespace: "native-test" }));
      builder.onLoad({ filter: /.*/, namespace: "native-test" }, args => ({ loader: "js", contents: args.path === "language" ? 'export const useLanguage = () => ({language:"en", t:(phrase,...args) => (phrase ?? "").replace(/\\{(\\d+)\\}/g, (token, index) => args[Number(index)] === undefined ? token : String(args[Number(index)] ?? ""))});' : args.path === "react-native"
        ? 'export const Keyboard = {dismiss(){}}; export const StyleSheet = {create: value => value}; export const Text = "Text"; export const View = "View";'
        : args.path === "@react-navigation/native" ? 'import React from "react"; export const useFocusEffect = callback => React.useEffect(callback, [callback]);'
        : args.path === "lucide-react-native" ? 'export const CirclePower = () => null; export const RefreshCcw = () => null; export const Zap = () => null;'
        : args.path === "auth" ? 'export const useAuth = () => globalThis.__deviceCommandAuth;'
        : 'import React from "react"; export const AppButton = props => React.createElement("button", props, props.label); export const Card = "Card"; export const ErrorBanner = "ErrorBanner"; export const TextField = "TextField"; export const EmptyState = "EmptyState"; export const StatusPill = "StatusPill"; export const Header = "Header"; export const LoadingState = "LoadingState"; export const Screen = "Screen";'
      }));
    } }]
  });
  const module = { exports: {} as { DevicesScreen: React.ComponentType } };
  new Function("require", "module", "exports", bundle.outputFiles[0]!.text)(createRequire(path.join(process.cwd(), "package.json")), module, module.exports);
  return module.exports.DevicesScreen;
}

for (const lostResponse of [false, true]) {
  test(`device UI preserves observed OFF for a ${lostResponse ? "lost response" : "pending receipt"} and only refreshes after matching acknowledgement`, async () => {
    const calls: { path: string; method: string; body: any }[] = [];
    let inventoryReads = 0;
    const receipt: SocketCommandReceipt = { deviceId: "socket-a", commandId: "fixed-command", isOn: true, status: "pending", rejection: null, createdAt: "2026-10-04T10:00:00Z", completedAt: null };
    const client = new ApiClient({ baseUrl: "https://solar.example", token: "owner", transport: async (url, init) => {
      const route = new URL(url).pathname;
      calls.push({ path: route, method: init.method, body: init.body ? JSON.parse(init.body) : null });
      if (init.method === "POST" && lostResponse) throw new Error("Lost response");
      const result = route === "/api/devices" ? { devices: [
        { id: "socket-a", name: "Heater", category: null, online: true, isOn: ++inventoryReads > 1, currentPowerW: 0 },
        { id: "socket-b", name: "Neighbor", category: null, online: true, isOn: false, currentPowerW: 0 }
      ], lastUpdated: null }
        : init.method === "POST" ? receipt
        : route.endsWith("/fixed-command") ? { ...receipt, status: "acknowledged" } : [];
      return { status: 200, ok: true, text: async () => JSON.stringify(result) };
    } });
    const api = {
      socketCommands: new SocketCommandCoordinator(new IntegrationApi(client), () => "fixed-command"),
      getDevices: (force: boolean) => client.request("/api/devices", { query: { refresh: force } }),
      setDeviceState: async () => assert.fail("The v1 command route cannot be used by real sessions.")
    };
    const globals = globalThis as typeof globalThis & { __deviceCommandAuth?: unknown; IS_REACT_ACT_ENVIRONMENT?: boolean };
    globals.__deviceCommandAuth = { api, isDemo: false };
    globals.IS_REACT_ACT_ENVIRONMENT = true;
    let renderer: ReturnType<typeof create> | undefined;
    try {
      const Devices = await component();
      await act(async () => { renderer = create(React.createElement(Devices)); });
      const card = (name: string) => renderer!.root.findAllByType("Card").find(item => item.findAllByType("Text").some(text => text.props.children === name))!;
      const button = (name: string, label: string) => card(name).findAllByType("button").find(item => item.props.label === label)!;
      await act(async () => { button("Heater", "ON").props.onPress(); });
      assert.equal(inventoryReads, 1);
      assert.equal(card("Heater").findByType("StatusPill").props.label, "OFF");
      assert.equal(button("Heater", "ON").props.disabled, true);
      assert.equal(button("Heater", "OFF").props.disabled, true);
      assert.equal(card("Heater").findAllByType("button").some(item => item.props.label === "Allow another command"), lostResponse);
      if (lostResponse) assert.match(card("Heater").findAllByType("Text").map(item => String(item.props.children)).join(" "), /may still finish.*does not cancel/);
      assert.equal(button("Neighbor", "ON").props.disabled, false);
      assert.equal(calls.filter(call => call.method === "POST").length, 1);
      assert.deepEqual(calls.find(call => call.method === "POST")!.body, { commandId: "fixed-command", isOn: true });
      assert.doesNotMatch(card("Heater").findAllByType("Text").map(item => String(item.props.children)).join(" "), /switched on/i);
      await act(async () => { button("Heater", "Check command result").props.onPress(); });
      assert.equal(inventoryReads, 2);
      assert.equal(card("Heater").findByType("StatusPill").props.label, "ON");
      assert.equal(card("Neighbor").findByType("StatusPill").props.label, "OFF");
      assert.equal(calls.filter(call => call.method === "POST").length, 1);
      assert.equal(button("Heater", "ON").props.disabled, false);
    } finally {
      if (renderer) await act(async () => renderer!.unmount());
      delete globals.__deviceCommandAuth;
      delete globals.IS_REACT_ACT_ENVIRONMENT;
    }
  });
}
