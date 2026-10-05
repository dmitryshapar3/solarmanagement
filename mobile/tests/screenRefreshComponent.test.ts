import assert from "node:assert/strict";
import { test } from "node:test";
import { createRequire } from "node:module";
import path from "node:path";
import React from "react";
import { act, create } from "react-test-renderer";
import { build } from "esbuild";

type Call = { method: string; args: unknown[] };
const globals = globalThis as typeof globalThis & {
  IS_REACT_ACT_ENVIRONMENT?: boolean;
  __screenRefreshApi?: unknown;
  __screenRefreshRoute?: { params: { period: string; date: string } };
};

async function screens() {
  const bundle = await build({
    stdin: { contents: `
      export { DashboardScreen } from "./src/features/dashboard/DashboardScreen";
      export { GenerationScreen } from "./src/features/generation/GenerationScreen";
      export { SolarEstimateDetailsScreen } from "./src/features/generation/SolarEstimateDetailsScreen";
      export { SalesScreen } from "./src/features/sales/SalesScreen";
      export { SalesDetailsScreen } from "./src/features/sales/SalesDetailsScreen";
      export { SettingsScreen } from "./src/features/settings/SettingsScreen";
    `, resolveDir: process.cwd(), loader: "ts" },
    bundle: true, write: false, platform: "node", format: "cjs", external: ["react", "react/jsx-runtime"],
    plugins: [{ name: "screen-refresh-boundaries", setup(builder) {
      builder.onResolve({ filter: /^(react-native|react-native-safe-area-context|lucide-react-native|@react-navigation\/native)$/ }, args => ({ path: args.path, namespace: "screen-test" }));
      builder.onResolve({ filter: /(?:^|\/)application\/(AuthContext|LanguageContext)$/ }, args => ({ path: args.path.endsWith("AuthContext") ? "auth" : "language", namespace: "screen-test" }));
      builder.onResolve({ filter: /(?:^|\/)i18n$/ }, () => ({ path: "i18n", namespace: "screen-test" }));
      builder.onResolve({ filter: /(?:^|\/)EnergyChart$/ }, () => ({ path: "chart", namespace: "screen-test" }));
      builder.onResolve({ filter: /^expo-crypto$/ }, () => ({ path: "crypto", namespace: "screen-test" }));
      builder.onResolve({ filter: /(?:^|\/)(AccountIdentityCard|AccountSecurityCard|IntegrationSettings|LanguageDropdown)$/ }, args => ({ path: args.path.split("/").at(-1)!, namespace: "settings-child" }));
      builder.onLoad({ filter: /.*/, namespace: "settings-child" }, args => ({ loader: "js", contents: `export const ${args.path}=()=>null;` }));
      builder.onLoad({ filter: /.*/, namespace: "screen-test" }, args => ({ loader: "js", contents:
        args.path === "react-native" ? `
          export const AppState={currentState:"active",addEventListener:()=>({remove(){}})};
          export const StyleSheet={create:value=>value,hairlineWidth:1};
          export const Text="Text",View="View",Pressable="Pressable",ScrollView="ScrollView",RefreshControl="RefreshControl";
          export const ActivityIndicator="ActivityIndicator",Switch="Switch",TextInput="TextInput",Linking={openURL:async()=>{}};
          export const Keyboard={dismiss(){}};
        ` : args.path === "react-native-safe-area-context" ? 'export const SafeAreaView="SafeAreaView";'
          : args.path === "lucide-react-native" ? 'export const ArrowRight=()=>null,CirclePower=()=>null,PlugZap=()=>null,LogOut=()=>null,MapPin=()=>null,Save=()=>null;'
          : args.path === "@react-navigation/native" ? `
            import React from "react";
            export const useFocusEffect=callback=>React.useEffect(callback,[callback]);
            export const useNavigation=()=>({navigate(){}}),useRoute=()=>globalThis.__screenRefreshRoute??{params:{}};
          ` : args.path === "auth" ? 'export const useAuth=()=>({api:globalThis.__screenRefreshApi,isDemo:true,apiBaseUrl:"https://solar.example",logout:async()=>{},updateApiBaseUrl:async()=>{}});'
          : args.path === "language" ? 'import { translate } from "i18n"; export const useLanguage=()=>({language:"en",t:translate});'
          : args.path === "i18n" ? 'export const currentLocale=()=>"en",formattingLocale=()=>"en-GB"; export const translate=(phrase,...args)=>(phrase??"").replace(/\\{(\\d+)\\}/g,(token,index)=>args[Number(index)]===undefined?token:String(args[Number(index)]??""));'
          : args.path === "crypto" ? 'export const randomUUID=()=>"test-command-id";'
          : 'export const EnergyChart="EnergyChart";'
      }));
    } }]
  });
  const module = { exports: {} as Record<string, React.ComponentType> };
  new Function("require", "module", "exports", bundle.outputFiles[0]!.text)(createRequire(path.join(process.cwd(), "package.json")), module, module.exports);
  return module.exports;
}

function mockApi(calls: Call[], delayedHistory?: () => Promise<unknown>) {
  const dashboard = { inverter: null, manualDevices: [], devices: [], rules: [], timeZoneId: "UTC" };
  let historyCalls = 0;
  const record = (method: string, args: unknown[], value: unknown) => { calls.push({ method, args }); return Promise.resolve(value); };
  return {
    sessionEpoch: 0, onSessionChange: () => () => {},
    socketCommands: { sessionEpoch: 0, subscribe: () => () => {}, get: () => null, isRunning: () => false },
    getDashboard: (...args: unknown[]) => record("dashboard", args, dashboard),
    refreshDashboard: (...args: unknown[]) => record("inverter", args, dashboard),
    getSolarEstimate: (...args: unknown[]) => record("estimate", args, null),
    getSolarHistory: (period: string, date?: string, ...args: unknown[]) => {
      calls.push({ method: "history", args: [period, date, ...args] });
      if (++historyCalls > 1 && delayedHistory) return delayedHistory();
      return Promise.resolve({ points: [], start: "2026-10-05T00:00:00Z", end: "2026-10-06T00:00:00Z", today: "2026-10-05", selectedDate: date ?? "2026-10-05", timeZoneId: "UTC" });
    },
    getSales: (...args: unknown[]) => record("sales", args, null)
  };
}

function refreshControl(renderer: ReturnType<typeof create>) {
  return renderer.root.findByType("ScrollView").props.refreshControl;
}

test("dashboard pull refresh reloads every card once and keeps spinning until the slowest card finishes", async () => {
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  const calls: Call[] = [];
  let finish!: (value: unknown) => void;
  const slow = new Promise(resolve => { finish = resolve; });
  globals.__screenRefreshApi = mockApi(calls, () => slow);
  let renderer: ReturnType<typeof create> | undefined;
  try {
    const Component = (await screens()).DashboardScreen!;
    await act(async () => { renderer = create(React.createElement(Component)); });
    assert.deepEqual(calls.map(call => call.method).sort(), ["dashboard", "estimate", "history", "sales"]);
    assert.equal(renderer!.root.findByType("ScrollView").props.alwaysBounceVertical, true);
    assert.equal(renderer!.root.findAllByType("Pressable").some(button => /Refresh|Retry/.test(button.props.accessibilityLabel ?? "")), false);
    calls.length = 0;
    const gesture = refreshControl(renderer!).props.onRefresh;
    await act(async () => { gesture(); gesture(); });
    assert.deepEqual(calls.map(call => call.method).sort(), ["estimate", "history", "inverter", "sales"]);
    assert.equal(refreshControl(renderer!).props.refreshing, true);
    await act(async () => { refreshControl(renderer!).props.onRefresh(); });
    assert.equal(calls.length, 4, "A pull while any card is pending cannot start another refresh");
    await act(async () => { finish(null); await slow; });
    assert.equal(refreshControl(renderer!).props.refreshing, false);
  } finally {
    await act(async () => { renderer?.unmount(); });
    delete globals.__screenRefreshApi;
    delete globals.IS_REACT_ACT_ENVIRONMENT;
  }
});

test("generation and detail pulls reload the selected history window with its snapshot and inverter", async () => {
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  const components = await screens();
  let renderer: ReturnType<typeof create> | undefined;
  try {
    for (const name of ["GenerationScreen", "SolarEstimateDetailsScreen"]) {
      const calls: Call[] = [];
      globals.__screenRefreshApi = mockApi(calls);
      globals.__screenRefreshRoute = { params: { period: "Month", date: "2026-10-04" } };
      await act(async () => { renderer = create(React.createElement(components[name]!)); });
      if (name === "GenerationScreen") {
        await act(async () => { renderer!.root.findAllByType("Pressable").find(button => button.props.accessibilityLabel === "7 days")!.props.onPress(); });
      }
      const selectedWindow = calls.filter(call => call.method === "history").at(-1)!.args.slice(0, 2);
      calls.length = 0;
      await act(async () => { refreshControl(renderer!).props.onRefresh(); });
      assert.deepEqual(calls.map(call => call.method).sort(), ["estimate", "history", "inverter"], name);
      assert.deepEqual(calls.find(call => call.method === "history")!.args.slice(0, 2), selectedWindow, name);
      assert.equal(refreshControl(renderer!).props.refreshing, false);
      await act(async () => { renderer!.unmount(); }); renderer = undefined;
    }
  } finally {
    await act(async () => { renderer?.unmount(); });
    delete globals.__screenRefreshApi;
    delete globals.__screenRefreshRoute;
    delete globals.IS_REACT_ACT_ENVIRONMENT;
  }
});

test("sales and sales detail pulls retain the selected reporting period and date", async () => {
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  const components = await screens();
  let renderer: ReturnType<typeof create> | undefined;
  try {
    for (const name of ["SalesScreen", "SalesDetailsScreen"]) {
      const calls: Call[] = [];
      globals.__screenRefreshApi = mockApi(calls);
      globals.__screenRefreshRoute = { params: { period: "Month", date: "2026-09-01" } };
      await act(async () => { renderer = create(React.createElement(components[name]!)); });
      if (name === "SalesScreen") {
        await act(async () => { renderer!.root.findAllByType("Pressable").find(button => button.props.accessibilityLabel === "Year")!.props.onPress(); });
      }
      const window = calls.filter(call => call.method === "sales").at(-1)!.args.slice(0, 2);
      calls.length = 0;
      await act(async () => { refreshControl(renderer!).props.onRefresh(); });
      assert.deepEqual(calls.map(call => call.method), ["sales"], name);
      assert.deepEqual(calls[0]!.args.slice(0, 2), window, name);
      await act(async () => { renderer!.unmount(); }); renderer = undefined;
    }
  } finally {
    await act(async () => { renderer?.unmount(); });
    delete globals.__screenRefreshApi;
    delete globals.__screenRefreshRoute;
    delete globals.IS_REACT_ACT_ENVIRONMENT;
  }
});

test("settings block pulls during the initial read and recover initial errors through the same gesture", async () => {
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  const calls: Call[] = [];
  let rejectInitial!: (error: Error) => void;
  const initial = new Promise((_, reject) => { rejectInitial = reject; });
  const settings = { polling: { intervalSeconds: 30 }, display: { timeZoneId: "Europe/Warsaw" } };
  const api = { ...mockApi(calls),
    getSettings: () => { calls.push({ method: "settings", args: [] }); return calls.filter(call => call.method === "settings").length === 1 ? initial : Promise.resolve(settings); },
    getSiteSettings: () => { calls.push({ method: "site", args: [] }); return Promise.resolve({ solarEstimate: {}, solarSales: {}, selectedDeviceSn: "" }); }
  };
  globals.__screenRefreshApi = api;
  let renderer: ReturnType<typeof create> | undefined;
  try {
    const Component = (await screens()).SettingsScreen!;
    await act(async () => { renderer = create(React.createElement(Component)); });
    assert.equal(refreshControl(renderer!).props.refreshing, true);
    await act(async () => { refreshControl(renderer!).props.onRefresh(); });
    assert.equal(calls.filter(call => call.method === "settings").length, 1, "The initial read cannot overlap a pull");
    await act(async () => { rejectInitial(new Error("Temporary settings outage")); });
    assert.equal(refreshControl(renderer!).props.refreshing, false);
    assert.equal(renderer!.root.findByType("ScrollView").props.alwaysBounceVertical, true);
    assert.equal(renderer!.root.findAllByType("Pressable").some(button => /Refresh|Retry/.test(button.props.accessibilityLabel ?? "")), false);
    await act(async () => { refreshControl(renderer!).props.onRefresh(); });
    assert.equal(calls.filter(call => call.method === "settings").length, 2);
    assert.equal(calls.filter(call => call.method === "site").length, 1);
    assert.equal(refreshControl(renderer!).props.refreshing, false);
  } finally {
    await act(async () => { renderer?.unmount(); });
    delete globals.__screenRefreshApi;
    delete globals.IS_REACT_ACT_ENVIRONMENT;
  }
});
