import assert from "node:assert/strict";
import { test } from "node:test";
import { createRequire } from "node:module";
import path from "node:path";
import React from "react";
import { act, create } from "react-test-renderer";
import { build } from "esbuild";
import type { useFocusedResource } from "../src/features/energy/useFocusedResource";

async function resourceHook() {
  const bundle = await build({
    stdin: { contents: 'export { useFocusedResource } from "./src/features/energy/useFocusedResource";', resolveDir: process.cwd(), loader: "ts" },
    bundle: true, write: false, platform: "node", format: "cjs", external: ["react", "react/jsx-runtime"],
    plugins: [{ name: "resource-lifecycle-boundaries", setup(builder) {
      builder.onResolve({ filter: /^(react-native|@react-navigation\/native)$|(?:^|\/)i18n$/ }, args => ({ path: args.path, namespace: "resource-test" }));
      builder.onLoad({ filter: /.*/, namespace: "resource-test" }, args => ({ loader: "js", contents:
        args.path === "react-native" ? 'export const AppState={currentState:"active",addEventListener:()=>({remove(){}})};'
          : args.path === "@react-navigation/native" ? 'import React from "react"; export const useFocusEffect=callback=>React.useEffect(callback,[callback]);'
          : 'export const translate=text=>text;'
      }));
    } }]
  });
  const module = { exports: {} as { useFocusedResource: typeof useFocusedResource } };
  new Function("require", "module", "exports", bundle.outputFiles[0]!.text)(createRequire(path.join(process.cwd(), "package.json")), module, module.exports);
  return module.exports.useFocusedResource;
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  return { promise: new Promise<T>(done => { resolve = done; }), resolve: (value: T) => resolve(value) };
}

test("the actual focused resource fences a replaced API with the same window and cannot release a newer filter query", async () => {
  const hook = await resourceHook();
  const globals = globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT?: boolean };
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  const old = deferred<string>(), replacement = deferred<string>(), filtered = deferred<string>();
  let oldSignal!: AbortSignal, filteredSignal!: AbortSignal;
  const oldFetch = async (signal: AbortSignal) => { oldSignal = signal; return old.promise; };
  const replacementFetch = async () => replacement.promise;
  const filteredFetch = async (signal: AbortSignal) => { filteredSignal = signal; return filtered.promise; };
  let resource!: ReturnType<typeof hook<string>>;
  function Probe({ window, read }: { window: string; read: (signal: AbortSignal) => Promise<string> }) { resource = hook(window, read); return null; }
  let renderer: ReturnType<typeof create> | undefined;
  try {
    await act(async () => { renderer = create(React.createElement(Probe, { window: "history:runs:6:ALL", read: oldFetch })); });
    await act(async () => { renderer!.update(React.createElement(Probe, { window: "history:runs:6:ALL", read: replacementFetch })); });
    assert.equal(oldSignal.aborted, true);
    await act(async () => { old.resolve("Previous account"); await old.promise; });
    assert.equal(resource.data, null); assert.equal(resource.loading, true);
    await act(async () => { replacement.resolve("Current account"); await replacement.promise; });
    assert.equal(resource.data, "Current account"); assert.equal(resource.loading, false);
    await act(async () => { renderer!.update(React.createElement(Probe, { window: "history:runs:6:ON", read: filteredFetch })); });
    assert.equal(resource.data, null); assert.equal(resource.loading, true);
    await act(async () => { renderer!.unmount(); }); renderer = undefined;
    assert.equal(filteredSignal.aborted, true);
    await act(async () => { filtered.resolve("Disposed selection"); await filtered.promise; });
    assert.equal(resource.data, null);
  } finally {
    await act(async () => { renderer?.unmount(); }); delete globals.IS_REACT_ACT_ENVIRONMENT;
  }
});

test("revoking the resource session hides retained values and fences an in-flight refresh", async () => {
  const hook = await resourceHook();
  const globals = globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT?: boolean };
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  const observers = new Set<() => void>();
  const session = { sessionEpoch: 0, onSessionChange: (observer: () => void) => { observers.add(observer); return () => { observers.delete(observer); }; } };
  const delayed = deferred<string>(); let calls = 0, signal!: AbortSignal;
  const read = async (nextSignal: AbortSignal) => { signal = nextSignal; return ++calls === 1 ? "Before revocation" : delayed.promise; };
  let resource!: ReturnType<typeof hook<string>>;
  function Probe() { resource = hook("history", read, session); return null; }
  let renderer: ReturnType<typeof create> | undefined;
  try {
    await act(async () => { renderer = create(React.createElement(Probe)); });
    assert.equal(resource.data, "Before revocation");
    let refresh!: Promise<void>;
    await act(async () => { refresh = resource.refresh(true); });
    await act(async () => { session.sessionEpoch++; for (const observer of observers) observer(); });
    assert.equal(signal.aborted, true); assert.equal(resource.data, null); assert.equal(resource.loading, false);
    await act(async () => { delayed.resolve("Late private result"); await refresh; });
    assert.equal(resource.data, null); assert.equal(calls, 2, "Revocation never retries a read automatically");
  } finally {
    await act(async () => { renderer?.unmount(); }); delete globals.IS_REACT_ACT_ENVIRONMENT;
  }
  assert.equal(observers.size, 0);
});
