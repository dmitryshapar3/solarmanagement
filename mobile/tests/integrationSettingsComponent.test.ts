import assert from "node:assert/strict";
import { test } from "node:test";
import { createRequire } from "node:module";
import path from "node:path";
import React from "react";
import { act, create } from "react-test-renderer";
import { build } from "esbuild";
import { ApiClient } from "../src/core/api/ApiClient";
import { IntegrationApi, type IntegrationCatalog, type IntegrationConfigurationChange } from "../src/core/api/IntegrationApi";
import { integrationFixture } from "./support/integrationFixture";

test("manufacturer dropdowns and mixed socket links use the real form and guarded API without discarding credential drafts", async () => {
  const { provider, configuration } = integrationFixture();
  const makeProvider = (id: string, name: string, kind: string) => ({ ...provider, providerId: id, displayName: name,
    requiredUiFeatures: [...provider.requiredUiFeatures, "wizard", "groups", "instructions"],
    uiLayout: { version: 1, steps: [{ id: kind, title: "Connect", groups: [{ id: "credentials", title: "Account", fieldKeys: provider.fields.map(field => field.key), actions: ["test", "discover"] }] }] } });
  const providers = [makeProvider("solis.cloud", "SolisCloud", "inverter"), makeProvider("huawei.fusionsolar", "Huawei", "inverter"),
    makeProvider("shelly.cloud", "Shelly", "socket"), makeProvider("tuya.cloud", "Tuya", "socket")];
  const source = "11111111-1111-4111-8111-111111111111";
  const settings = ["shelly", "tuya"].map((name, index) => ({ ...structuredClone(configuration), instance: {
    ...configuration.instance, id: `instance-${name}`, name: `${name} account`, providerId: providers[index + 2]!.providerId, status: "enabled"
  } }));
  const devices = settings.map((setting, index) => ({ id: `socket-${index}`, instanceId: setting.instance.id, kind: "socket", name: index ? "Desk plug" : "Three-phase heater",
    remoteId: "same-vendor-id", enabled: true, isDefault: false, sourceInverterId: null as string | null, phaseCount: 1 as 1 | 3 }));
  const writes: any[] = [];
  const api = new IntegrationApi(new ApiClient({ baseUrl: "https://solar.example", transport: async (url, init) => {
    const route = new URL(url).pathname;
    const index = settings.findIndex(setting => route.includes(`/${setting.instance.id}/`));
    let result: unknown = [];
    if (route.endsWith("integration-providers")) result = { providers, revision: "1" };
    else if (route.endsWith("integration-socket-sources")) result = [{ id: source, name: "Battery inverter", isDefault: false }];
    else if (route === "/api/v2/integrations") result = settings.map(setting => setting.instance);
    else if (route.endsWith("/versions")) result = providers;
    else if (route.endsWith("/configuration")) result = settings[index];
    else if (route.endsWith("/devices")) result = [devices[index]];
    else if (route.endsWith("/source")) {
      const body = JSON.parse(init.body!); writes.push({ instance: settings[index]!.instance.id, body });
      assert.equal(body.expectedSourceInverterId, devices[index]!.sourceInverterId);
      assert.equal(body.expectedPhaseCount, devices[index]!.phaseCount);
      devices[index] = { ...devices[index]!, sourceInverterId: body.sourceInverterId, phaseCount: body.phaseCount };
      settings[index]!.instance.generation++;
      result = devices[index];
    } else throw new Error(`Unexpected request: ${route}`);
    return { status: 200, ok: true, text: async () => JSON.stringify(result) };
  } }));
  const globals = globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT?: boolean; __integrationFocus?: unknown };
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  let renderer: ReturnType<typeof create> | undefined;
  try {
    const IntegrationSettings = await component();
    await act(async () => { renderer = create(React.createElement(IntegrationSettings, { api })); });
    const button = (label: string) => renderer!.root.findAllByType("button").find(item => item.props.label === label)!;
    const dropdown = (label: string) => renderer!.root.findAllByType("button").find(item => item.props.accessibilityLabel === label)!;
    await act(async () => { dropdown("Inverter manufacturer").props.onPress(); });
    assert.ok(button("SolisCloud")); assert.ok(button("Huawei")); assert.equal(button("Tuya"), undefined);
    await act(async () => { button("SolisCloud").props.onPress(); dropdown("Socket manufacturer").props.onPress(); });
    assert.ok(button("Shelly")); assert.ok(button("Tuya")); assert.equal(button("Huawei"), undefined);
    await act(async () => { button("Shelly").props.onPress(); button("Configure shelly account").props.onPress(); });
    await act(async () => { renderer!.root.findByProps({ label: "Region *" }).props.onChangeText("unsaved-region"); });
    await act(async () => { dropdown("Linked inverter for Three-phase heater").props.onPress(); });
    await act(async () => { button("Battery inverter").props.onPress(); dropdown("Circuit type for Three-phase heater").props.onPress(); });
    await act(async () => { button("Three-phase").props.onPress(); });
    await act(async () => { button("Save link for Three-phase heater").props.onPress(); });
    assert.equal(renderer!.root.findByProps({ label: "Region *" }).props.value, "unsaved-region");
    assert.equal(settings[0]!.values.region, "eu");
    await act(async () => { button("Configure tuya account").props.onPress(); });
    await act(async () => { dropdown("Linked inverter for Desk plug").props.onPress(); });
    await act(async () => { button("Battery inverter").props.onPress(); });
    await act(async () => { button("Save link for Desk plug").props.onPress(); });
    assert.deepEqual(devices.map(device => [device.sourceInverterId, device.phaseCount]), [[source, 3], [source, 1]]);
    await act(async () => { button("Configure shelly account").props.onPress(); });
    await act(async () => { dropdown("Linked inverter for Three-phase heater").props.onPress(); });
    await act(async () => { button("Installation default inverter").props.onPress(); });
    await act(async () => { button("Save link for Three-phase heater").props.onPress(); });
    assert.equal(writes[2].body.sourceInverterId, null);
    assert.equal(writes[2].body.expectedSourceInverterId, source);
    assert.equal(writes[2].body.expectedPhaseCount, 3);
    assert.equal(writes[0].body.guard.packageDigest, configuration.instance.packageDigest);
    assert.equal(devices[1]!.sourceInverterId, source);
  } finally {
    if (renderer) await act(async () => renderer!.unmount());
    delete globals.IS_REACT_ACT_ENVIRONMENT; delete globals.__integrationFocus;
  }
});

async function component() {
  const bundle = await build({
    stdin: { contents: 'export { IntegrationSettings } from "./src/features/integrations/IntegrationSettings";', resolveDir: process.cwd(), loader: "ts" },
    bundle: true, write: false, platform: "node", format: "cjs", external: ["react", "react/jsx-runtime"],
    plugins: [{ name: "integration-native-boundaries", setup(builder) {
      builder.onResolve({ filter: /core\/api\/ApiClient$/ }, () => ({ path: path.join(process.cwd(), "src/core/api/ApiClient.ts"), external: true }));
      builder.onResolve({ filter: /^(react-native|@react-navigation\/native|expo-web-browser)$/ }, args => ({ path: args.path, namespace: "native-test" }));
      builder.onResolve({ filter: /(?:^|\/)application\/LanguageContext$/ }, () => ({ path: "language", namespace: "native-test" }));
      builder.onResolve({ filter: /(?:^|\/)core\/components$/ }, () => ({ path: "components", namespace: "native-test" }));
      builder.onLoad({ filter: /.*/, namespace: "native-test" }, args => ({
        loader: "js", contents: args.path === "language" ? 'export const useLanguage = () => ({language:"en", t:(phrase,...args) => (phrase ?? "").replace(/\\{(\\d+)\\}/g, (token, index) => args[Number(index)] === undefined ? token : String(args[Number(index)] ?? ""))});' : args.path === "react-native"
          ? 'export const Keyboard = {dismiss(){}}; export const StyleSheet = {create: value => value}; export const Text = "Text"; export const View = "View";'
          : args.path === "expo-web-browser" ? 'export const openAuthSessionAsync = (...args) => globalThis.__integrationOAuthBrowser.open(...args); export const dismissAuthSession = () => globalThis.__integrationOAuthBrowser.dismiss();'
          : args.path === "@react-navigation/native"
            ? 'import React from "react"; export const useFocusEffect = callback => { globalThis.__integrationFocus = callback; React.useEffect(callback, [callback]); };'
            : 'import React from "react"; export const AppButton = props => React.createElement("button", props, props.label); export const Card = "Card"; export const SectionTitle = "SectionTitle"; export const ErrorBanner = "ErrorBanner"; export const TextField = "TextField"; export const EmptyState = "EmptyState"; export const StatusPill = "StatusPill"; export const SwitchRow = "SwitchRow";'
      }));
    } }]
  });
  const module = { exports: {} as { IntegrationSettings: React.ComponentType<any> } };
  new Function("require", "module", "exports", bundle.outputFiles[0]!.text)(createRequire(path.join(process.cwd(), "package.json")), module, module.exports);
  return module.exports.IntegrationSettings;
}

test("declarative wizard navigation retains hidden drafts, renders plain instructions and puts discovery in its active group", async () => {
  const { provider, configuration } = integrationFixture();
  provider.requiredUiFeatures.push("wizard", "groups", "instructions", "conditional-fields", "device-selector");
  provider.fields.find(field => field.key === "mode")!.options!.push({ value: "local", label: "Local" });
  provider.fields.find(field => field.key === "interval")!.activeWhen = { field: "readOnly", operator: "eq", value: false };
  provider.uiLayout = { version: 1, steps: [
    { id: "connect", title: "Connection", instructions: "<script>plain instruction</script>", groups: [
      { id: "account", title: "Account", fieldKeys: ["region", "mode", "token"], actions: ["test"], instructions: "Credentials remain private." }
    ] },
    { id: "resources", title: "Resources", groups: [
      { id: "polling", title: "Polling", fieldKeys: ["readOnly", "interval"], actions: ["discover"], activeWhen: { field: "mode", operator: "eq", value: "cloud" } }
    ] }
  ] };
  let saved = structuredClone(configuration);
  const calls: { route: string; method: string; body: any }[] = [];
  const api = new IntegrationApi(new ApiClient({ baseUrl: "https://solar.example", transport: async (url, init) => {
    const route = new URL(url).pathname;
    const body = init.body ? JSON.parse(init.body) : undefined;
    calls.push({ route, method: init.method, body });
    let result: unknown = [];
    if (route.endsWith("integration-providers")) result = { providers: [provider], revision: "1" };
    else if (route === "/api/v2/integrations") result = [saved.instance];
    else if (route.endsWith("/configuration")) {
      if (init.method === "PUT") saved = { ...saved, instance: { ...saved.instance, revision: saved.instance.revision + 1 }, values: body.values };
      result = saved;
    } else if (route.endsWith("/versions")) result = [provider];
    else if (route.endsWith("/discovery")) result = { devices: [{ selectionToken: "wizard-proof", name: "Wizard socket", kind: "socket", remoteId: "one" }], expiresAt: "2099-01-01T00:00:00Z" };
    return { status: 200, ok: true, text: async () => JSON.stringify(result) };
  } }));
  const globals = globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT?: boolean; __integrationFocus?: unknown };
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  let renderer: ReturnType<typeof create> | undefined;
  try {
    const IntegrationSettings = await component();
    await act(async () => { renderer = create(React.createElement(IntegrationSettings, { api })); });
    const button = (label: string) => renderer!.root.findAllByType("button").find(item => item.props.label === label)!;
    const field = (label: string) => renderer!.root.findAllByType("TextField").find(item => item.props.label === label);
    await act(async () => { button("Configure My equipment").props.onPress(); });
    assert.ok(renderer!.root.findAllByType("Text").some(item => item.props.children === "<script>plain instruction</script>"));
    assert.equal(field("Interval *"), undefined);
    assert.equal(button("Discover draft devices"), undefined);
    await act(async () => { field("Region *")!.props.onChangeText("unsaved-region"); button("Next step").props.onPress(); });
    assert.equal(field("Interval *")!.props.value, "10");
    await act(async () => { field("Interval *")!.props.onChangeText("17"); });
    await act(async () => { button("Discover draft devices").props.onPress(); });
    assert.ok(button("Use Wizard socket"));
    assert.equal(button("Use Wizard socket").props.disabled, true);
    assert.equal(calls.filter(call => call.method === "PUT").length, 0);
    assert.equal(calls.find(call => call.route.endsWith("/discovery"))!.body.values.interval, 17);
    await act(async () => { renderer!.root.findByType("SwitchRow").props.onValueChange(true); });
    assert.equal(field("Interval *"), undefined);
    await act(async () => { renderer!.root.findByType("SwitchRow").props.onValueChange(false); });
    assert.equal(field("Interval *")!.props.value, "17");
    await act(async () => { button("Previous step").props.onPress(); });
    assert.equal(field("Region *")!.props.value, "unsaved-region");
    await act(async () => { button("Local").props.onPress(); button("Next step").props.onPress(); });
    assert.equal(field("Interval *"), undefined);
    assert.equal(button("Discover draft devices"), undefined);
    await act(async () => { button("Save integration settings").props.onPress(); });
    assert.equal(saved.values.interval, 17);
    assert.equal(saved.values.region, "unsaved-region");
    assert.equal(saved.values.mode, "local");
    assert.equal(calls.filter(call => call.method === "PUT").length, 1);
    assert.equal(configuration.values.region, "eu");
    assert.equal(configuration.values.interval, 10);
  } finally {
    if (renderer) await act(async () => renderer!.unmount());
    delete globals.__integrationFocus; delete globals.IS_REACT_ACT_ENVIRONMENT;
  }
});

test("real OAuth form authorizes a private draft, invalidates it on edit and consumes it only on explicit Save", async () => {
  const { provider, configuration } = integrationFixture();
  provider.actions.push("oauth"); provider.requiredUiFeatures.push("oauth");
  provider.oauthDefinition = { secretFieldKeys: ["token"] };
  configuration.secretPresent.token = false;
  let saved = structuredClone(configuration);
  let flowCounter = 0;
  let currentFlow = "";
  const calls: { route: string; method: string; body: any }[] = [];
  const client = new ApiClient({ baseUrl: "https://solar.example", token: "owner-one", transport: async (url, init) => {
    const route = new URL(url).pathname;
    const body = init.body ? JSON.parse(init.body) : undefined;
    calls.push({ route, method: init.method, body });
    let result: unknown = [];
    if (route.endsWith("integration-providers")) result = { providers: [provider], revision: "1" };
    else if (route === "/api/v2/integrations") result = [saved.instance];
    else if (route.endsWith("/configuration")) {
      if (init.method === "PUT") saved = { ...saved, instance: { ...saved.instance, revision: saved.instance.revision + 1 }, values: body.values, secretPresent: { token: true } };
      result = saved;
    } else if (route.endsWith("/versions")) result = [provider];
    else if (route.endsWith("/oauth/start")) {
      currentFlow = `11111111-1111-4111-8111-${String(++flowCounter).padStart(12, "0")}`;
      result = { flowId: currentFlow, returnNonce: "this-flow-nonce", returnUri: "deyesolar://integration-oauth", authorizationUrl: "https://identity.example/authorize", expiresAt: "2099-01-01T00:00:00Z" };
    } else if (route.includes("/oauth/")) result = { flowId: currentFlow, status: route.endsWith("/cancel") ? "cancelled" : "ready", expiresAt: "2099-01-01T00:00:00Z", values: { region: "authorized-account" }, secretPresent: { token: true } };
    return { status: 200, ok: true, text: async () => JSON.stringify(result) };
  } });
  const globals = globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT?: boolean; __integrationFocus?: unknown;
    __integrationOAuthBrowser?: { open(): Promise<{ type: string; url: string }>; dismiss(): void } };
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  globals.__integrationOAuthBrowser = { open: async () => ({ type: "success", url: `deyesolar://integration-oauth?flowId=${currentFlow}&returnNonce=this-flow-nonce` }), dismiss() {} };
  let renderer: ReturnType<typeof create> | undefined;
  try {
    const IntegrationSettings = await component();
    await act(async () => { renderer = create(React.createElement(IntegrationSettings, { api: new IntegrationApi(client) })); });
    const button = (label: string) => renderer!.root.findAllByType("button").find(item => item.props.label === label)!;
    await act(async () => { button("Configure My equipment").props.onPress(); });
    assert.equal(renderer!.root.findAllByType("button").some(item => item.props.accessibilityLabel === "API key: replace"), false);
    await act(async () => { button("Authorize provider").props.onPress(); });
    assert.equal(renderer!.root.findByProps({ label: "Region *" }).props.value, "authorized-account");
    assert.ok(renderer!.root.findAllByType("Text").some(item => String(item.props.children).includes("Authorization is ready in this draft")));
    assert.equal(saved.secretPresent.token, false);
    assert.equal(calls.some(call => call.method === "PUT"), false);
    const firstFlow = currentFlow;
    await act(async () => { renderer!.root.findByProps({ label: "Region *" }).props.onChangeText("edited-after-authorization"); });
    assert.equal(calls.filter(call => call.route.endsWith(`/${firstFlow}/cancel`)).length, 1);
    await act(async () => { button("Save integration settings").props.onPress(); });
    assert.equal(calls.some(call => call.method === "PUT"), false);
    assert.match(renderer!.root.findAllByType("ErrorBanner").map(item => item.props.message).join(" "), /API key is required/);
    await act(async () => { button("Authorize provider").props.onPress(); });
    await act(async () => { button("Save integration settings").props.onPress(); });
    const write = calls.find(call => call.method === "PUT")!;
    assert.equal(write.body.oauthFlowId, currentFlow);
    assert.deepEqual(write.body.secretOperations, { token: { operation: "keep" } });
    assert.equal(saved.secretPresent.token, true);
    assert.equal(calls.filter(call => call.route.endsWith(`/${currentFlow}/cancel`)).length, 0);
    assert.equal(button("Save integration settings").props.disabled, false);
    assert.equal(renderer!.root.findAllByType("TextField").some(item => item.props.secureTextEntry), false);
    assert.equal(configuration.values.region, "eu");
    assert.equal(configuration.secretPresent.token, false);
  } finally {
    if (renderer) await act(async () => renderer!.unmount());
    delete globals.__integrationFocus; delete globals.__integrationOAuthBrowser; delete globals.IS_REACT_ACT_ENVIRONMENT;
  }
});

test("explicit OAuth cancel and a same-client account switch release busy state and fence late callbacks", async () => {
  const { provider, configuration } = integrationFixture();
  provider.actions.push("oauth"); provider.requiredUiFeatures.push("oauth"); provider.oauthDefinition = { secretFieldKeys: ["token"] };
  configuration.secretPresent.token = false;
  const calls: { route: string; token?: string }[] = [];
  const client = new ApiClient({ baseUrl: "https://solar.example", token: "owner-one", transport: async (url, init) => {
    const route = new URL(url).pathname;
    calls.push({ route, token: init.headers.Authorization });
    const result = route.endsWith("integration-providers") ? { providers: [provider], revision: "1" }
      : route === "/api/v2/integrations" ? init.headers.Authorization === "Bearer owner-one" ? [configuration.instance] : []
      : route.endsWith("/configuration") ? configuration : route.endsWith("/versions") ? [provider]
      : route.endsWith("/oauth/start") ? { flowId: "flow-one", returnNonce: "nonce", authorizationUrl: "https://identity.example/authorize", returnUri: "deyesolar://integration-oauth", expiresAt: "2099-01-01T00:00:00Z" } : [];
    return { status: 200, ok: true, text: async () => JSON.stringify(result) };
  } });
  let finish!: (result: { type: string; url: string }) => void;
  let dismissals = 0;
  const globals = globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT?: boolean; __integrationFocus?: unknown;
    __integrationOAuthBrowser?: { open(): Promise<{ type: string; url: string }>; dismiss(): void } };
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  globals.__integrationOAuthBrowser = { open: () => new Promise(resolve => { finish = resolve; }), dismiss() { ++dismissals; } };
  let renderer: ReturnType<typeof create> | undefined;
  try {
    const IntegrationSettings = await component();
    await act(async () => { renderer = create(React.createElement(IntegrationSettings, { api: new IntegrationApi(client) })); });
    const button = (label: string) => renderer!.root.findAllByType("button").find(item => item.props.label === label);
    await act(async () => { button("Configure My equipment")!.props.onPress(); });
    await act(async () => { button("Authorize provider")!.props.onPress(); });
    assert.ok(button("Cancel authorization"));
    await act(async () => { button("Cancel authorization")!.props.onPress(); });
    assert.equal(button("Cancel authorization"), undefined);
    assert.equal(button("Save integration settings")!.props.disabled, false);
    assert.equal(calls.filter(call => call.route.endsWith("/cancel")).length, 1);
    await act(async () => { finish({ type: "success", url: "deyesolar://integration-oauth?flowId=flow-one&returnNonce=nonce" }); });
    assert.equal(renderer!.root.findByProps({ label: "Region *" }).props.value, "eu");
    await act(async () => { button("Authorize provider")!.props.onPress(); });
    assert.ok(button("Cancel authorization"));
    await act(async () => { client.setToken("owner-two"); });
    assert.equal(button("Cancel authorization"), undefined);
    assert.equal(button("Save integration settings"), undefined);
    assert.equal(button("Configure My equipment"), undefined);
    assert.equal(button("Refresh integration catalog"), undefined);
    assert.equal(renderer!.root.findAllByType("button").find(item => item.props.accessibilityLabel === "Inverter manufacturer")!.props.disabled, false);
    await act(async () => { finish({ type: "success", url: "deyesolar://integration-oauth?flowId=flow-one&returnNonce=nonce" }); });
    assert.equal(dismissals, 2);
    assert.equal(calls.filter(call => call.route.endsWith("/cancel")).length, 1);
    assert.equal(calls.some(call => /\/oauth\/flow-one$/.test(call.route)), false);
    assert.equal(button("Save integration settings"), undefined);
  } finally {
    if (renderer) await act(async () => renderer!.unmount());
    delete globals.__integrationFocus; delete globals.__integrationOAuthBrowser; delete globals.IS_REACT_ACT_ENVIRONMENT;
  }
});

test("a newly cataloged provider uses the real generic form; probes never save and explicit selection follows saving", async () => {
  const { provider, configuration } = integrationFixture();
  const catalog: IntegrationCatalog = { providers: [provider], revision: "1" };
  const calls: { method: string; path: string; body: any }[] = [];
  let saved = structuredClone(configuration);
  const client = new ApiClient({ baseUrl: "https://solar.example", token: "installation-owner", transport: async (url, init) => {
    const route = new URL(url).pathname;
    const body = init.body ? JSON.parse(init.body) : undefined;
    calls.push({ method: init.method, path: route, body });
    let result: unknown = {};
    if (route === "/api/v2/integration-providers") result = catalog;
    else if (route === "/api/v2/integrations") result = [saved.instance];
    else if (route.endsWith("/configuration") && init.method === "GET") result = saved;
    else if (route.endsWith("/configuration") && init.method === "PUT") {
      saved = { instance: { ...saved.instance, revision: saved.instance.revision + 1 }, values: body.values, secretPresent: { token: true } };
      result = saved;
    } else if (route.endsWith("/test")) result = { success: true, code: "connected", message: "Draft connected." };
    else if (route.endsWith("/versions")) result = [provider];
    else if (route.endsWith("/discovery")) result = { devices: [{ selectionToken: "proof", name: "New inverter", kind: "inverter", remoteId: "vendor-id" }], expiresAt: "2099-01-01T00:00:00Z" };
    else if (route.endsWith("/devices")) result = [];
    else if (route.endsWith("/devices/selection")) result = { id: "binding", instanceId: saved.instance.id, kind: "inverter", name: "New inverter", remoteId: "vendor-id", isDefault: true };
    else throw new Error(`Unexpected request: ${route}`);
    return { status: 200, ok: true, text: async () => JSON.stringify(result) };
  } });
  const globals = globalThis as typeof globalThis & { __integrationFocus?: () => () => void; IS_REACT_ACT_ENVIRONMENT?: boolean };
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  let renderer: ReturnType<typeof create> | undefined;
  try {
    const IntegrationSettings = await component();
    await act(async () => { renderer = create(React.createElement(IntegrationSettings, { api: new IntegrationApi(client) })); });
    const button = (label: string) => renderer!.root.findAllByType("button").find(item => item.props.label === label)!;
    const field = (label: string) => renderer!.root.findAllByType("TextField").find(item => item.props.label === label)!;
    await act(async () => { button("Configure My equipment").props.onPress(); });
    assert.equal(renderer!.root.findAllByType("TextField").some(item => item.props.secureTextEntry), false);
    await act(async () => { field("Region *").props.onChangeText("us"); });
    await act(async () => {
      renderer!.root.findAllByType("button").find(item => item.props.accessibilityLabel === "API key: Replace")!.props.onPress();
    });
    await act(async () => { field("New API key").props.onChangeText("draft-new-key"); });
    await act(async () => { button("Test draft connection").props.onPress(); });
    await act(async () => { button("Discover draft devices").props.onPress(); });
    assert.equal(button("Use New inverter").props.disabled, true);
    assert.equal(calls.some(call => call.method === "PUT" || call.path.endsWith("/devices/selection")), false);
    const probe = calls.find(call => call.path.endsWith("/test"))!;
    assert.deepEqual(probe.body, {
      expectedRevision: 7, packageVersion: "1.0.0", packageDigest: "package-a", descriptorDigest: "schema-a",
      values: { region: "us", interval: 10, mode: "cloud", readOnly: false }, secretOperations: { token: { operation: "replace", value: "draft-new-key" } }
    });
    // A focus/catalog refresh must expose new providers without replacing an existing unsaved draft.
    catalog.providers.push({ ...provider, providerId: "another-manufacturer", displayName: "Another manufacturer" });
    await act(async () => { globals.__integrationFocus!(); });
    await act(async () => { renderer!.root.findAllByType("button").find(item => item.props.accessibilityLabel === "Other provider")!.props.onPress(); });
    assert.ok(button("Another manufacturer"));
    assert.equal(field("Region *").props.value, "us");
    assert.equal(field("New API key").props.value, "draft-new-key");
    await act(async () => { button("Save integration settings").props.onPress(); });
    assert.equal(saved.values.region, "us");
    assert.equal(renderer!.root.findAllByType("TextField").some(item => item.props.secureTextEntry), false);
    assert.equal(button("Use New inverter"), undefined);
    await act(async () => { button("Discover draft devices").props.onPress(); });
    assert.equal(button("Use New inverter").props.disabled, false);
    await act(async () => { button("Use New inverter").props.onPress(); });
    const selection = calls.find(call => call.path.endsWith("/devices/selection"))!;
    assert.equal(selection.body.selectionToken, "proof");
    assert.equal(selection.body.draft.expectedRevision, 8);
    assert.deepEqual(selection.body.draft.secretOperations, { token: { operation: "keep" } });
    assert.equal(calls.filter(call => call.method === "PUT").length, 1);
    assert.equal(calls.some(call => call.path.endsWith("/state")), false);
  } finally {
    if (renderer) await act(async () => renderer!.unmount());
    delete globals.__integrationFocus;
    delete globals.IS_REACT_ACT_ENVIRONMENT;
  }
});

test("a revision conflict preserves the actual form draft and releases busy state; required unknown UI prevents writes", async () => {
  const { provider, configuration } = integrationFixture();
  const calls: string[] = [];
  const client = new ApiClient({ baseUrl: "https://solar.example", transport: async (url, init) => {
    const route = new URL(url).pathname;
    calls.push(`${init.method} ${route}`);
    const conflict = init.method === "PUT";
    const result = conflict ? { code: "configuration_conflict", message: "A newer revision exists." }
      : route.endsWith("integration-providers") ? { providers: [provider], revision: "1" }
      : route === "/api/v2/integrations" ? [configuration.instance]
      : route.endsWith("configuration") ? configuration : [];
    return { status: conflict ? 409 : 200, ok: !conflict, text: async () => JSON.stringify(result) };
  } });
  const globals = globalThis as typeof globalThis & { __integrationFocus?: () => () => void; IS_REACT_ACT_ENVIRONMENT?: boolean };
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  let renderer: ReturnType<typeof create> | undefined;
  try {
    const IntegrationSettings = await component();
    const api = new IntegrationApi(client);
    await act(async () => { renderer = create(React.createElement(IntegrationSettings, { api })); });
    const button = (label: string) => renderer!.root.findAllByType("button").find(item => item.props.label === label)!;
    await act(async () => { button("Configure My equipment").props.onPress(); });
    await act(async () => { renderer!.root.findByProps({ label: "Region *" }).props.onChangeText("draft-still-here"); });
    await act(async () => { button("Save integration settings").props.onPress(); });
    assert.equal(renderer!.root.findByProps({ label: "Region *" }).props.value, "draft-still-here");
    assert.equal(button("Save integration settings").props.disabled, false);
    assert.match(renderer!.root.findAllByType("ErrorBanner").map(item => item.props.message).join(" "), /Your draft is preserved/);
    assert.equal(calls.filter(call => call.startsWith("PUT")).length, 1);
    assert.equal(calls.filter(call => call === "GET /api/v2/integrations/instance-a/configuration").length, 1);
    provider.fields.push({ key: "new-control", kind: "new-native-component", label: "Required custom control", required: true, secret: false, options: [] });
    provider.descriptorDigest = "schema-new";
    configuration.instance.descriptorDigest = "schema-new";
    await act(async () => { globals.__integrationFocus!(); });
    await act(async () => { button("Reload saved settings (discard draft)").props.onPress(); });
    assert.equal(button("Save integration settings").props.disabled, true);
    assert.equal(button("Test draft connection").props.disabled, true);
    assert.equal(button("Discover draft devices").props.disabled, true);
    assert.equal(calls.filter(call => call.startsWith("PUT")).length, 1);
  } finally {
    if (renderer) await act(async () => renderer!.unmount());
    delete globals.__integrationFocus;
    delete globals.IS_REACT_ACT_ENVIRONMENT;
  }
});

test("a pinned older integration loads its own schema and sends its original package identity", async () => {
  const { provider, configuration } = integrationFixture();
  provider.fields[0]!.options = null;
  const latest = { ...provider, packageVersion: "2.0.0", packageDigest: "package-b", descriptorDigest: "schema-b" };
  const calls: { route: string; body: unknown }[] = [];
  const client = new ApiClient({ baseUrl: "https://solar.example", transport: async (url, init) => {
    const route = new URL(url).pathname;
    const body = init.body ? JSON.parse(init.body) : undefined;
    calls.push({ route, body });
    const result = route.endsWith("integration-providers") ? { providers: [latest], revision: "2" }
      : route === "/api/v2/integrations" ? [configuration.instance]
      : route.endsWith("configuration") ? configuration
      : route.endsWith("/versions/1.0.0/ui") ? provider
      : route.endsWith("/test") ? { success: true, code: "connected", message: "Connected." } : [];
    return { status: 200, ok: true, text: async () => JSON.stringify(result) };
  } });
  const globals = globalThis as typeof globalThis & { __integrationFocus?: () => () => void; IS_REACT_ACT_ENVIRONMENT?: boolean };
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  let renderer: ReturnType<typeof create> | undefined;
  try {
    const IntegrationSettings = await component();
    await act(async () => { renderer = create(React.createElement(IntegrationSettings, { api: new IntegrationApi(client) })); });
    const button = (label: string) => renderer!.root.findAllByType("button").find(item => item.props.label === label)!;
    await act(async () => { button("Configure My equipment").props.onPress(); });
    assert.ok(calls.some(call => call.route === "/api/v2/integration-providers/new-manufacturer/versions/1.0.0/ui"));
    await act(async () => { button("Test draft connection").props.onPress(); });
    const change = calls.find(call => call.route.endsWith("/test"))!.body as IntegrationConfigurationChange;
    assert.equal(change.packageVersion, "1.0.0");
    assert.equal(change.packageDigest, "package-a");
    assert.equal(change.descriptorDigest, "schema-a");
    assert.equal(change.expectedRevision, 7);
  } finally {
    if (renderer) await act(async () => renderer!.unmount());
    delete globals.__integrationFocus;
    delete globals.IS_REACT_ACT_ENVIRONMENT;
  }
});

for (const catalogFailure of ["unavailable", "tampered"] as const) test(`an enabled instance can be stopped when its catalog is ${catalogFailure} and its provider schema unavailable`, async () => {
  const { configuration } = integrationFixture();
  configuration.instance.status = "enabled";
  const neighbor = { ...configuration.instance, id: "neighbor-instance", name: "Neighbor equipment" };
  const calls: { route: string; body: unknown }[] = [];
  const client = new ApiClient({ baseUrl: "https://solar.example", transport: async (url, init) => {
    const route = new URL(url).pathname;
    calls.push({ route, body: init.body ? JSON.parse(init.body) : undefined });
    const unavailable = route.includes("integration-providers");
    const result = unavailable ? { message: "Provider unavailable." }
      : route === "/api/v2/integrations" ? [configuration.instance, neighbor]
      : route.endsWith("configuration") ? configuration
      : route.endsWith("/disable") ? { ...configuration.instance, status: "disabled", revision: 8 } : [];
    return { status: unavailable && catalogFailure === "unavailable" ? 503 : 200,
      ok: !unavailable || catalogFailure === "tampered",
      text: async () => unavailable && catalogFailure === "tampered" ? "tampered-provider-payload" : JSON.stringify(result) };
  } });
  const globals = globalThis as typeof globalThis & { __integrationFocus?: () => () => void; IS_REACT_ACT_ENVIRONMENT?: boolean };
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  let renderer: ReturnType<typeof create> | undefined;
  try {
    const IntegrationSettings = await component();
    await act(async () => { renderer = create(React.createElement(IntegrationSettings, { api: new IntegrationApi(client) })); });
    const button = (label: string) => renderer!.root.findAllByType("button").find(item => item.props.label === label)!;
    assert.equal(button("Disable My equipment").props.disabled, false);
    await act(async () => { button("Configure My equipment").props.onPress(); });
    await act(async () => { button("Disable My equipment").props.onPress(); });
    assert.deepEqual(calls.find(call => call.route.endsWith("/disable"))!.body, {
      expectedRevision: 7, packageVersion: "1.0.0", packageDigest: "package-a", descriptorDigest: "schema-a"
    });
    assert.equal(button("Disable My equipment"), undefined);
    assert.equal(button("Disable Neighbor equipment").props.disabled, false);
    assert.equal(calls.some(call => call.route.endsWith("/enable") || call.route.endsWith("/test")), false);
  } finally {
    if (renderer) await act(async () => renderer!.unmount());
    delete globals.__integrationFocus;
    delete globals.IS_REACT_ACT_ENVIRONMENT;
  }
});

test("changing accounts clears the previous form and busy operation before the new account catalog arrives", async () => {
  const first = integrationFixture();
  const second = integrationFixture();
  second.configuration.instance.id = "instance-b";
  second.configuration.instance.name = "Second account equipment";
  let finishCatalog!: () => void;
  const heldCatalog = new Promise<void>(resolve => { finishCatalog = resolve; });
  let firstProbeSignal: AbortSignal | undefined;
  const apiFor = (fixture: typeof first, hold: boolean) => new IntegrationApi(new ApiClient({
    baseUrl: "https://solar.example", token: hold ? "owner-b" : "owner-a", transport: async (url, init) => {
      const route = new URL(url).pathname;
      if (route.endsWith("/test") && !hold) {
        firstProbeSignal = init.signal;
        await new Promise<void>(() => {});
      }
      if (route.endsWith("integration-providers") && hold) await heldCatalog;
      const result = route.endsWith("integration-providers") ? { providers: [fixture.provider], revision: "1" }
        : route === "/api/v2/integrations" ? [fixture.configuration.instance]
        : route.endsWith("configuration") ? fixture.configuration : [];
      return { status: 200, ok: true, text: async () => JSON.stringify(result) };
    }
  }));
  const globals = globalThis as typeof globalThis & { __integrationFocus?: () => () => void; IS_REACT_ACT_ENVIRONMENT?: boolean };
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  let renderer: ReturnType<typeof create> | undefined;
  try {
    const IntegrationSettings = await component();
    await act(async () => { renderer = create(React.createElement(IntegrationSettings, { api: apiFor(first, false) })); });
    const button = (label: string) => renderer!.root.findAllByType("button").find(item => item.props.label === label)!;
    await act(async () => { button("Configure My equipment").props.onPress(); });
    await act(async () => { renderer!.root.findByProps({ label: "Region *" }).props.onChangeText("private-account-a-draft"); });
    await act(async () => { button("Test draft connection").props.onPress(); });
    assert.equal(button("Test draft connection").props.loading, true);
    await act(async () => { renderer!.update(React.createElement(IntegrationSettings, { api: apiFor(second, true) })); });
    assert.equal(firstProbeSignal!.aborted, true);
    assert.equal(Boolean(button("Configure My equipment")), false);
    assert.equal(renderer!.root.findAllByType("TextField").some(item => item.props.value === "private-account-a-draft"), false);
    assert.ok(renderer!.root.findAllByType("Text").some(item => item.props.children === "Loading integrations..."));
    await act(async () => { finishCatalog(); });
    assert.equal(button("Configure Second account equipment").props.disabled, false);
    await act(async () => { button("Configure Second account equipment").props.onPress(); });
    assert.equal(renderer!.root.findByProps({ label: "Region *" }).props.value, second.configuration.values.region);
    assert.equal(button("Save integration settings").props.disabled, false);
  } finally {
    finishCatalog();
    if (renderer) await act(async () => renderer!.unmount());
    delete globals.__integrationFocus;
    delete globals.IS_REACT_ACT_ENVIRONMENT;
  }
});

test("an explicit version switch sends the saved revision guard and discards the draft only after success", async () => {
  const { provider, configuration } = integrationFixture();
  const next = { ...provider, packageVersion: "2.0.0", packageDigest: "package-b", descriptorDigest: "schema-b" };
  let saved = structuredClone(configuration);
  let conflict = true;
  const switches: any[] = [];
  const api = new IntegrationApi(new ApiClient({ baseUrl: "https://solar.example", transport: async (url, init) => {
    const route = new URL(url).pathname;
    let result: unknown = [];
    if (route.endsWith("integration-providers")) result = { providers: [next], revision: "2" };
    else if (route === "/api/v2/integrations") result = [saved.instance];
    else if (route.endsWith("/versions")) result = [provider, next];
    else if (route.endsWith("/versions/1.0.0/ui")) result = provider;
    else if (route.endsWith("configuration")) result = saved;
    else if (route.endsWith("/package")) {
      switches.push(JSON.parse(init.body!));
      if (conflict) return { status: 409, ok: false, text: async () => JSON.stringify({ message: "The package changed." }) };
      saved = { ...saved, instance: { ...saved.instance, ...next, revision: 8, generation: 9 } };
      result = saved.instance;
    }
    return { status: 200, ok: true, text: async () => JSON.stringify(result) };
  } }));
  const globals = globalThis as typeof globalThis & { __integrationFocus?: () => () => void; IS_REACT_ACT_ENVIRONMENT?: boolean };
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  let renderer: ReturnType<typeof create> | undefined;
  try {
    const IntegrationSettings = await component();
    await act(async () => { renderer = create(React.createElement(IntegrationSettings, { api })); });
    const button = (label: string) => renderer!.root.findAllByType("button").find(item => item.props.label === label)!;
    await act(async () => { button("Configure My equipment").props.onPress(); });
    await act(async () => { renderer!.root.findByProps({ label: "Region *" }).props.onChangeText("unsaved-draft"); });
    await act(async () => { button("Version 2.0.0").props.onPress(); });
    assert.equal(renderer!.root.findByProps({ label: "Region *" }).props.value, "unsaved-draft");
    await act(async () => { button("Switch package version (discard draft)").props.onPress(); });
    assert.equal(renderer!.root.findByProps({ label: "Region *" }).props.value, "unsaved-draft");
    assert.deepEqual(switches[0], { guard: { expectedRevision: 7, packageVersion: "1.0.0", packageDigest: "package-a", descriptorDigest: "schema-a" }, targetPackageVersion: "2.0.0" });
    conflict = false;
    await act(async () => { button("Switch package version (discard draft)").props.onPress(); });
    assert.equal(renderer!.root.findByProps({ label: "Region *" }).props.value, "eu");
    assert.equal(button("Switch package version (discard draft)").props.disabled, true);
    assert.equal(switches.length, 2);
    assert.equal(saved.instance.packageVersion, "2.0.0");
    assert.equal(saved.instance.revision, 8);
  } finally {
    if (renderer) await act(async () => renderer!.unmount());
    delete globals.__integrationFocus;
    delete globals.IS_REACT_ACT_ENVIRONMENT;
  }
});
