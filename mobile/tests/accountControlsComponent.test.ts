import assert from "node:assert/strict";
import { test } from "node:test";
import { createRequire } from "node:module";
import path from "node:path";
import React from "react";
import { act, create } from "react-test-renderer";
import { build } from "esbuild";
import { languages } from "../src/core/i18n";

async function components() {
  const bundle = await build({
    stdin: { contents: 'export { LanguageDropdown } from "./src/features/settings/LanguageDropdown"; export { GoogleSignInButton } from "./src/features/auth/GoogleSignInButton";', resolveDir: process.cwd(), loader: "ts" },
    bundle: true, write: false, platform: "node", format: "cjs", external: ["react", "react/jsx-runtime"],
    plugins: [{ name: "native-controls", setup(builder) {
      builder.onResolve({ filter: /^(react-native|react-native-svg|lucide-react-native)$|application\/LanguageContext$|core\/components$|ui\/theme\/ThemeProvider$/ }, args => ({ path: args.path, namespace: "native-controls" }));
      builder.onLoad({ filter: /.*/, namespace: "native-controls" }, args => ({ loader: "js", contents:
        args.path === "react-native" ? 'export const Pressable="Pressable", Text="Text", View="View", ScrollView="ScrollView", ActivityIndicator="ActivityIndicator"; export const StyleSheet={create:v=>v};'
          : args.path === "react-native-svg" ? 'export default "Svg"; export const Path="Path";'
          : args.path === "lucide-react-native" ? 'export const Check="Check", ChevronDown="ChevronDown";'
          : args.path.endsWith("components") ? 'export const ThemedText="Text";'
          : args.path.endsWith("ThemeProvider") ? 'export const useTheme=()=>({colors:{ink:"#111",ink3:"#888",line:"#ddd",surface:"#fff"}});'
          : 'import React from "react"; export function useLanguage(){ const [language,set]=React.useState("ru"); return {language,languages:globalThis.__solarControlsLanguages,t:value=>value,setLanguage:async value=>{set(value);}}; }'
      }));
    } }]
  });
  const module = { exports: {} as any };
  new Function("require", "module", "exports", bundle.outputFiles[0]!.text)(createRequire(path.join(process.cwd(), "package.json")), module, module.exports);
  return module.exports;
}

test("language options stay in a collapsed dropdown and selection closes it", async () => {
  const globals = globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT?: boolean; __solarControlsLanguages?: typeof languages };
  globals.IS_REACT_ACT_ENVIRONMENT = true; globals.__solarControlsLanguages = languages;
  let renderer: ReturnType<typeof create> | undefined;
  try {
    const { LanguageDropdown } = await components();
    await act(async () => { renderer = create(React.createElement(LanguageDropdown, { onError: () => {} })); });
    assert.equal(renderer.root.findAllByType("Pressable").length, 1);
    assert.equal(renderer.root.findByType("Text").props.children, "Русский");
    await act(async () => { renderer!.root.findByType("Pressable").props.onPress(); });
    assert.equal(renderer.root.findAllByType("Pressable").length, 16);
    assert.equal(renderer.root.findByType("ScrollView").props.nestedScrollEnabled, true);
    await act(async () => { renderer!.root.findByProps({ accessibilityLabel: "Deutsch" }).props.onPress(); });
    assert.equal(renderer.root.findAllByType("Pressable").length, 1);
    assert.equal(renderer.root.findByType("Text").props.children, "Deutsch");
    assert.equal(renderer.root.findByType("Pressable").props.accessibilityState.expanded, false);
  } finally {
    if (renderer) await act(async () => renderer!.unmount());
    delete globals.IS_REACT_ACT_ENVIRONMENT; delete globals.__solarControlsLanguages;
  }
});

test("Google sign-in is an accessible logo control with a loading state", async () => {
  const globals = globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT?: boolean };
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  let renderer: ReturnType<typeof create> | undefined;
  try {
    const { GoogleSignInButton } = await components();
    await act(async () => { renderer = create(React.createElement(GoogleSignInButton, { onPress: () => {} })); });
    assert.equal(renderer.root.findByType("Pressable").props.accessibilityLabel, "Continue with Google");
    assert.equal(renderer.root.findAllByType("Text").length, 0);
    assert.equal(renderer.root.findAllByType("Path").length, 4);
    await act(async () => { renderer!.update(React.createElement(GoogleSignInButton, { onPress: () => {}, loading: true })); });
    assert.equal(renderer.root.findByType("Pressable").props.disabled, true);
    assert.equal(renderer.root.findByType("Pressable").props.accessibilityState.busy, true);
    assert.equal(renderer.root.findAllByType("ActivityIndicator").length, 1);
  } finally {
    if (renderer) await act(async () => renderer!.unmount());
    delete globals.IS_REACT_ACT_ENVIRONMENT;
  }
});
