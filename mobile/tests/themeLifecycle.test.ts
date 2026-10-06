import assert from "node:assert/strict";
import { test } from "node:test";
import React from "react";
import { act, create } from "react-test-renderer";
import { globals, uiHarness } from "./support/uiHarness";
const fixture = globalThis as typeof globalThis & { __themeStorage?: any; __themeValue?: any; __systemTheme?: string | null; __nativeSchemes?: string[] };
async function theme() {
  return uiHarness(`import React from "react"; import { ThemeProvider, useTheme, resolveAppearance } from "./src/ui/theme/ThemeProvider";
  export { resolveAppearance };function Capture(){globalThis.__themeValue=useTheme();return null;}export const TestTheme=()=>React.createElement(ThemeProvider,null,React.createElement(Capture));`, { stubs: {
    "@react-native-async-storage/async-storage": 'export default {getItem:key=>globalThis.__themeStorage.getItem(key),setItem:(key,value)=>globalThis.__themeStorage.setItem(key,value)};',
    "react-native": 'export const Appearance={setColorScheme(value){globalThis.__nativeSchemes?.push(value)} };export const useColorScheme=()=>globalThis.__systemTheme;'
  } });
}
test("appearance defaults to light, follows system only when chosen, and survives a slow older storage read", async () => {
  globals.IS_REACT_ACT_ENVIRONMENT = true; fixture.__systemTheme = "dark"; fixture.__nativeSchemes=[];
  let saved!: (value: string | null) => void; const pending = new Promise<string | null>(resolve => { saved = resolve; }); const writes: any[] = [];
  fixture.__themeStorage = { getItem: () => pending, setItem: async (...args: any[]) => { writes.push(args); } };
  let renderer: ReturnType<typeof create> | undefined;
  try { const Component = (await theme()).TestTheme!; await act(async () => { renderer = create(React.createElement(Component)); }); assert.equal(fixture.__themeValue.appearance, "light"); assert.equal(fixture.__themeValue.scheme, "light");
    await act(async () => { await fixture.__themeValue.setAppearance("system"); }); assert.equal(fixture.__themeValue.scheme, "dark"); assert.deepEqual(writes, [["solar.appearance.v1", "system"]]);
    await act(async () => { saved("light"); await pending; }); assert.equal(fixture.__themeValue.appearance, "system");
    fixture.__systemTheme = "light"; await act(async () => { renderer!.update(React.createElement(Component)); }); assert.equal(fixture.__themeValue.scheme, "light");
    await act(async () => { await fixture.__themeValue.setAppearance("dark"); }); assert.equal(fixture.__themeValue.scheme, "dark"); assert.deepEqual(fixture.__nativeSchemes,["light","unspecified","dark"]);
  } finally { await act(async () => renderer?.unmount()); delete globals.IS_REACT_ACT_ENVIRONMENT; delete fixture.__themeStorage; delete fixture.__themeValue; delete fixture.__systemTheme; delete fixture.__nativeSchemes; }
});
test("stored appearance is restored without accepting unknown persisted values", async () => {
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  try { const Component = (await theme()).TestTheme!; for (const stored of ["dark", "system", "unsupported"]) { fixture.__systemTheme = "dark"; fixture.__themeStorage = { getItem: async () => stored, setItem: async () => {} }; let renderer: ReturnType<typeof create> | undefined;
    await act(async () => { renderer = create(React.createElement(Component)); }); assert.equal(fixture.__themeValue.appearance, stored === "unsupported" ? "light" : stored); await act(async () => renderer?.unmount());
  } } finally { delete globals.IS_REACT_ACT_ENVIRONMENT; delete fixture.__themeStorage; delete fixture.__themeValue; delete fixture.__systemTheme; delete fixture.__nativeSchemes; }
});
