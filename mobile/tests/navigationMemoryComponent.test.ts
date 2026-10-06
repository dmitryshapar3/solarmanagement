import assert from "node:assert/strict";
import { test } from "node:test";
import { createRequire } from "node:module";
import path from "node:path";
import React from "react";
import { act, create } from "react-test-renderer";
import { build } from "esbuild";

const detailState = {
  index: 1,
  routes: [
    { name: "MainTabs", state: { index: 2, routes: [{ name: "HomeTab" }, { name: "Energy" }, { name: "Devices" }] } },
    { name: "ExportHourlySheet", params: { period: "Month", date: "2026-09-01" } }
  ]
};
const initialState = { index: 0, routes: [{ name: "MainTabs" }] };

async function harness() {
  const native = {
    auth: { api: { sessionEpoch: 1 }, apiBaseUrl: "https://solar.example", username: "owner",
      isAuthenticated: true, isBootstrapping: false, isDemo: false, logout: async () => {} },
    gateOpen: true,
    setGate: (_open: boolean) => {},
    select: (_state: unknown) => {},
    currentState: undefined as unknown,
    mounts: 0,
    unmounts: 0
  };
  const globals = globalThis as typeof globalThis & { __solarNavigationNative?: typeof native; IS_REACT_ACT_ENVIRONMENT?: boolean };
  globals.__solarNavigationNative = native;
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  const bundle = await build({
    stdin: { contents: 'export { AppNavigator } from "./src/application/AppNavigator";', resolveDir: process.cwd(), loader: "ts" },
    bundle: true, write: false, platform: "node", format: "cjs", external: ["react", "react/jsx-runtime"], define: { __DEV__: "false" },
    plugins: [{ name: "navigation-boundaries", setup(builder) {
      builder.onResolve({ filter: /^(react-native|lucide-react-native|react-native-safe-area-context|expo-blur|@bottom-tabs\/react-navigation|@react-navigation\/(native|bottom-tabs|native-stack))$/ }, args => ({ path: args.path, namespace: "navigation-test" }));
      builder.onResolve({ filter: /(?:^|\/)(AuthContext|LanguageContext|core\/(components|publicLinks))$/ }, args => ({ path: args.path, namespace: "navigation-test" }));
      builder.onResolve({ filter: /(?:^|\/)([A-Za-z]+Screen|[A-Za-z]+Sheet|AuthScreens|SettingsPages|SubscriptionContext)$/ }, args => ({ path: args.path.split("/").at(-1)!, namespace: "screen-test" }));
      builder.onResolve({ filter: /(?:^|\/)ThemeProvider$/ }, () => ({ path: "theme", namespace: "navigation-test" }));
      builder.onLoad({ filter: /.*/, namespace: "screen-test" }, args => ({ loader: "js", contents:
        args.path === "SubscriptionContext" ? 'import React from "react"; export const SubscriptionProvider=({children})=>React.createElement(React.Fragment,null,children);'
           : args.path === "AuthScreens" ? 'export const WelcomeScreen=()=>null,CodeRequestScreen=()=>null,EmailCodeScreen=()=>null,PasswordLoginScreen=()=>null,ServerScreen=()=>null;'
          : args.path === "SettingsPages" ? 'export const AppearanceScreen=()=>null,ChangePasswordScreen=()=>null,ConnectedServicesScreen=()=>null,DataRefreshScreen=()=>null,DeleteAccountScreen=()=>null,EditProfileScreen=()=>null,LanguageScreen=()=>null,PasswordSessionsScreen=()=>null,ServerSettingsScreen=()=>null,SignInSecurityScreen=()=>null,SolarSiteScreen=()=>null,TariffExportScreen=()=>null,TimeZoneScreen=()=>null;'
          : args.path === "SubscriptionScreen" ? `
            import React from "react";
            export function SubscriptionGate({children}) {
              const native=globalThis.__solarNavigationNative;
              const [open,setOpen]=React.useState(native.gateOpen);
              native.setGate=setOpen;
              return open?children:React.createElement("Paywall");
            }
            export const SubscriptionScreen=()=>null,DemoPaywallScreen=()=>null;
          ` : `export const ${args.path}=()=>null;`
      }));
      builder.onLoad({ filter: /.*/, namespace: "navigation-test" }, args => {
        const state = "globalThis.__solarNavigationNative";
        const contents = args.path === "react-native" ? 'export const Platform={OS:"ios"},StyleSheet={create:value=>value}; export const Text="Text",View="View",Pressable="Pressable",ActivityIndicator="ActivityIndicator";'
          : args.path === "lucide-react-native" ? 'export const House=()=>null,ChartNoAxesCombined=()=>null,Workflow=()=>null,Banknote=()=>null,CreditCard=()=>null,History=()=>null,LayoutDashboard=()=>null,MoreHorizontal=()=>null,PlugZap=()=>null,Settings=()=>null,SlidersHorizontal=()=>null,SunMedium=()=>null;'
          : args.path === "react-native-safe-area-context" ? 'export const SafeAreaProvider="SafeAreaProvider",SafeAreaView="SafeAreaView",useSafeAreaInsets=()=>({bottom:0});'
          : args.path === "expo-blur" ? 'export const BlurView="BlurView";'
          : args.path === "theme" ? 'export const useTheme=()=>({colors:{bg:"white",surface:"white",line:"gray",ink:"black",ink3:"gray"},scheme:"light"});'
          : args.path.includes("AuthContext") ? `export const useAuth=()=>${state}.auth;`
          : args.path.includes("LanguageContext") ? 'export const useLanguage=()=>({t:phrase=>phrase});'
          : args.path.includes("components") ? 'import React from "react";export const ScreenTopInsetContext=React.createContext(true);export const AppButton="AppButton",Banner="Banner",LoadingState="LoadingState",ThemedText="Text",Card="Card",ErrorBanner="ErrorBanner",Header="Header",Screen="Screen";'
          : args.path.includes("publicLinks") ? 'export const openPublicLink=async()=>{},PUBLIC_PRIVACY_URL="https://solar.example/privacy",PUBLIC_TERMS_URL="https://solar.example/terms",PUBLIC_SUPPORT_URL="https://solar.example/support";'
          : args.path === "@react-navigation/native" ? `
            import React from "react";
            export const DarkTheme={colors:{}},DefaultTheme={colors:{}};
            export function NavigationContainer({initialState,onStateChange,children}) {
              const native=${state};
              const [value,setValue]=React.useState(()=>initialState??${JSON.stringify(initialState)});
              native.currentState=value;
              native.select=next=>{setValue(next);onStateChange?.(next);};
              React.useEffect(()=>{native.mounts++;return()=>{native.unmounts++;};},[]);
              return React.createElement("NavigationContainer",{state:value},children);
            }
          ` : 'import React from "react"; const factory=()=>({Navigator:({children})=>React.createElement(React.Fragment,null,children),Screen:()=>null}); export const createBottomTabNavigator=factory,createNativeBottomTabNavigator=factory,createNativeStackNavigator=factory;';
        return { contents, loader: "js" };
      });
    } }]
  });
  const module = { exports: {} as { AppNavigator: React.ComponentType } };
  new Function("require", "module", "exports", bundle.outputFiles[0]!.text)(createRequire(path.join(process.cwd(), "package.json")), module, module.exports);
  let renderer!: ReturnType<typeof create>;
  await act(async () => { renderer = create(React.createElement(module.exports.AppNavigator)); });
  return {
    native,
    select: async () => { await act(async () => { native.select(detailState); }); },
    gate: async (open: boolean) => { await act(async () => { native.gateOpen = open; native.setGate(open); }); },
    update: async () => { await act(async () => { renderer.update(React.createElement(module.exports.AppNavigator)); }); },
    close: async () => {
      await act(async () => { renderer.unmount(); });
      delete globals.__solarNavigationNative;
      delete globals.IS_REACT_ACT_ENVIRONMENT;
    }
  };
}

test("the real AppNavigator restores the nested last route after gate-only revocation and reauthorization", async () => {
  const h = await harness();
  try {
    await h.select();
    assert.deepEqual(h.native.currentState, detailState);
    await h.gate(false);
    assert.equal(h.native.mounts, 1);
    assert.equal(h.native.unmounts, 1);
    // Only the subscription gate rerenders here: the route must be read at navigator
    // mount, rather than captured as undefined in the AppNavigator's old JSX.
    await h.gate(true);
    assert.equal(h.native.mounts, 2);
    assert.deepEqual(h.native.currentState, detailState);
  } finally { await h.close(); }
});

test("account, API, session epoch, logout and demo boundaries clear navigation memory", async () => {
  const h = await harness();
  try {
    for (const change of [
      () => { h.native.auth.username = "different-owner"; },
      () => { h.native.auth.apiBaseUrl = "https://other-solar.example"; },
      () => { h.native.auth.api.sessionEpoch++; },
      () => { h.native.auth.api = { sessionEpoch: 1 }; }
    ]) {
      await h.select();
      await h.gate(false);
      change();
      await h.update();
      await h.gate(true);
      assert.deepEqual(h.native.currentState, initialState);
    }
    await h.select();
    h.native.auth.isAuthenticated = false;
    await h.update();
    h.native.auth.isAuthenticated = true;
    await h.update();
    assert.deepEqual(h.native.currentState, initialState, "Reusing the same account after logout does not reuse its old route");
    await h.select();
    h.native.auth.isDemo = true;
    await h.update();
    assert.deepEqual(h.native.currentState, initialState);
    h.native.auth.isDemo = false;
    await h.update();
    assert.deepEqual(h.native.currentState, initialState, "Demo routes cannot enter a real account's navigation");
  } finally { await h.close(); }
});
