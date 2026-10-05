import assert from "node:assert/strict";
import { test } from "node:test";
import { createRequire } from "node:module";
import path from "node:path";
import React from "react";
import { act, create } from "react-test-renderer";
import { build } from "esbuild";
import type { Device } from "../src/core/api/types";

function deferred<T>() {
  let resolve!: (value: T) => void;
  return { promise: new Promise<T>(done => { resolve = done; }), resolve: (value: T) => resolve(value) };
}

test("the actual device-name editor fences late saves and keeps the replacement account's busy draft", async () => {
  const device: Device = { id: "8d62e8e7-763a-44d5-9a75-0637a3d66d36", name: "Old provider", category: null,
    online: true, isOn: false, stateKnown: true, currentPowerW: 0, cloudName: "Provider", localName: null };
  const old = deferred<Device>(), current = deferred<Device>();
  const api = (name: string, save: Promise<Device>) => ({ sessionEpoch: 0, onSessionChange: () => () => {},
    getDevices: async () => ({ devices: [{ ...device, name }], lastUpdated: null }),
    renameDevice: async () => save, socketCommands: { recover: async () => [], get: () => null, isRunning: () => false } });
  const state = { api: api("Old provider", old.promise), isDemo: false };
  const globals = globalThis as typeof globalThis & { __solarDeviceNames?: typeof state; IS_REACT_ACT_ENVIRONMENT?: boolean };
  globals.__solarDeviceNames = state; globals.IS_REACT_ACT_ENVIRONMENT = true;
  let renderer: ReturnType<typeof create> | undefined;
  try {
    const bundle = await build({
      stdin: { contents: 'export { DevicesScreen } from "./src/features/devices/DevicesScreen";', resolveDir: process.cwd(), loader: "ts" },
      bundle: true, write: false, platform: "node", format: "cjs", external: ["react", "react/jsx-runtime", "expo-crypto"],
      plugins: [{ name: "name-editor-boundaries", setup(builder) {
        builder.onResolve({ filter: /^(react-native|lucide-react-native|@react-navigation\/native)$|(?:^|\/)application\/(AuthContext|LanguageContext)$|(?:^|\/)core\/components$|(?:^|\/)i18n$|(?:^|\/)useSocketCommandActions$/ }, args => ({ path: args.path, namespace: "name-test" }));
        builder.onLoad({ filter: /.*/, namespace: "name-test" }, args => ({ loader: "js", contents:
          args.path === "react-native" ? 'export const StyleSheet={create:value=>value}; export const Text="Text",View="View",Keyboard={dismiss(){}};'
            : args.path === "lucide-react-native" ? 'export const CirclePower=()=>null,RefreshCcw=()=>null,Zap=()=>null;'
            : args.path === "@react-navigation/native" ? 'import React from "react"; export const useFocusEffect=callback=>React.useEffect(callback,[callback]);'
            : args.path.endsWith("AuthContext") ? 'export const useAuth=()=>globalThis.__solarDeviceNames;'
            : args.path.endsWith("LanguageContext") ? 'export const useLanguage=()=>({t:text=>text});'
            : args.path.endsWith("i18n") ? 'export const formattingLocale=()=>"en-GB"; export const translate=text=>text;'
            : args.path.endsWith("useSocketCommandActions") ? 'export const useSocketCommandActions=()=>({busy:()=>null,send:async()=>{},check:async()=>{}});'
            : 'export const AppButton="AppButton",Card="Card",EmptyState="EmptyState",ErrorBanner="ErrorBanner",Header="Header",LoadingState="LoadingState",Screen="Screen",TextField="TextField",StatusPill="StatusPill";'
        }));
      } }]
    });
    const module = { exports: {} as { DevicesScreen: React.ComponentType } };
    new Function("require", "module", "exports", bundle.outputFiles[0]!.text)(createRequire(path.join(process.cwd(), "package.json")), module, module.exports);
    const Component = module.exports.DevicesScreen;
    await act(async () => { renderer = create(React.createElement(Component)); });
    const button = (label: string) => renderer!.root.findAllByType("AppButton").find(item => item.props.label === label)!;
    const field = () => renderer!.root.findByType("TextField");
    await act(async () => { button("Edit name").props.onPress(); });
    await act(async () => { field().props.onChangeText("Old account draft"); });
    await act(async () => { button("Save name").props.onPress(); });
    assert.equal(button("Save name").props.loading, true);
    state.api = api("Current provider", current.promise);
    await act(async () => { renderer!.update(React.createElement(Component)); });
    await act(async () => { button("Edit name").props.onPress(); });
    await act(async () => { field().props.onChangeText("Current draft"); });
    await act(async () => { button("Save name").props.onPress(); });
    await act(async () => { old.resolve({ ...device, name: "Old account draft" }); await old.promise; });
    assert.equal(field().props.value, "Current draft");
    assert.equal(field().props.editable, false);
    assert.equal(button("Save name").props.loading, true, "Late old finally cannot release the current save");
    assert.ok(renderer!.root.findAllByType("Text").some(item => item.props.children === "Current provider"));
    await act(async () => { current.resolve({ ...device, name: "Current draft" }); await current.promise; });
    assert.ok(button("Edit name"));
    assert.ok(renderer!.root.findAllByType("Text").some(item => item.props.children === "Current draft"));
    assert.equal(renderer!.root.findByType("ErrorBanner").props.message, null);
  } finally {
    await act(async () => { renderer?.unmount(); }); delete globals.__solarDeviceNames; delete globals.IS_REACT_ACT_ENVIRONMENT;
  }
});
