import assert from "node:assert/strict";
import { test } from "node:test";
import React from "react";
import { act, create } from "react-test-renderer";
import { ApiClient } from "../src/core/api/ApiClient";
import { DeyeSolarApi } from "../src/core/api/DeyeSolarApi";
import type { IntegrationDeviceBinding } from "../src/core/api/IntegrationApi";
import type { BillingAccess } from "../src/features/subscription/billingPolicy";
import { canSelectSocket, discoveryFacts, matchingBinding, trialSocketRemaining } from "../src/features/connect/connectPolicy";
import { globals, uiHarness } from "./support/uiHarness";
import { integrationFixture } from "./support/integrationFixture";
const device = (token = "proof-a", remoteId = "remote-a") => ({ selectionToken: token, remoteId, name: "Provider plug", kind: "socket", channel: "0", metadata: { capabilities: { canSwitch: false, canMeasurePower: true } } });
function access(socketUsage = 0): BillingAccess { const now = Date.now(); return { status: "trial", hasAccess: true, trialEndsAt: new Date(now + 3600000).toISOString(), subscriptionExpiresAt: null, accessValidUntil: new Date(now + 3600000).toISOString(), appAccountToken: "00000000-0000-4000-8000-000000000001", socketLimit: 1, socketUsage, appleSubscriptionsEnabled: false, serverNow: new Date(now).toISOString() }; }
test("discovery reads canonical nested capabilities without inferring online or accepting flattened coercions", () => {
  assert.deepEqual(discoveryFacts(device()), { canSwitch: false, online: null });
  assert.deepEqual(discoveryFacts({ ...device(), metadata: { canSwitch: false, online: "true", capabilities: { canSwitch: "false" } } }), { canSwitch: null, online: null });
  assert.deepEqual(discoveryFacts({ ...device(), metadata: { online: false, capabilities: { canSwitch: true } } }), { canSwitch: true, online: false });
  assert.deepEqual(discoveryFacts({ ...device(), metadata: { online: true } }), { canSwitch: null, online: true });
});
test("trial quota requires actual integral server usage and matching bindings preserve channel identity", () => {
  assert.equal(canSelectSocket(access(0)), true); assert.equal(canSelectSocket(access(1)), false); assert.equal(canSelectSocket({ ...access(), socketUsage: undefined }), false); assert.equal(canSelectSocket({ ...access(), hasAccess: false }), false);
  assert.equal(trialSocketRemaining(access(0)), 1); assert.equal(trialSocketRemaining(access(1)), 0);
  for (const usage of [undefined, -1, 0.5, "0", NaN]) assert.equal(trialSocketRemaining({ ...access(), socketUsage: usage } as BillingAccess), null);
  assert.equal(trialSocketRemaining({ ...access(1), status: "active", socketLimit: null }), null);
  const binding: IntegrationDeviceBinding = { id: "bound-a", instanceId: "instance-a", kind: "socket", name: "Provider plug", remoteId: "remote-a", channel: "1", isDefault: false };
  assert.equal(matchingBinding(device(), [binding]), undefined); assert.equal(matchingBinding({ ...device(), channel: "1" }, [binding]), binding);
});
async function component() { return (await uiHarness('export { ConnectScreen } from "./src/features/connect/ConnectScreen";', { stubComponents: true, stubs: { components: 'import React from "react";export const ThemedText="Text",Screen="Screen",Header="Header",Card="Card",TextField="TextField",ErrorBanner="ErrorBanner",EmptyState="EmptyState",LoadingState="LoadingState",StatusPill="StatusPill",DataRow="DataRow",SegmentedControl="SegmentedControl";export const SectionTitle=({trailing,...props})=>React.createElement("SectionTitle",props,trailing);export const AppButton=props=>React.createElement("button",props,props.label);', IntegrationFields: 'export const IntegrationFields=()=>null;', integrationOAuth: 'export const authorizeIntegration=async()=>null;' } })).ConnectScreen!; }
function fixture(usage = 0, existing: IntegrationDeviceBinding[] = [], unavailableQuota = false, holdSelection = false) {
  const { provider, configuration } = integrationFixture(); const calls: { path: string; method: string; body: any }[] = []; let nextUsage = usage; let releaseSelection: (() => void) | undefined;
  const client = new ApiClient({ baseUrl: "https://solar.example", token: "fictional-owner", transport: async (url, init) => {
    const path = new URL(url).pathname; const body = init.body ? JSON.parse(init.body) : undefined; calls.push({ path, method: init.method, body }); let result: unknown;
    if (path === "/api/v2/integration-providers") result = { providers: [provider], revision: "fixture", providerKinds: { [provider.providerId]: ["socket"] } };
    else if (path.includes("/integration-providers/")) result = provider;
    else if (path === "/api/auth/security/permissions") result = { role: "Owner", permissions: ["Read", "ManageIntegrations"] };
    else if (path.endsWith("/configuration")) result = configuration;
    else if (path.endsWith("/discovery")) result = { devices: [device(), device("proof-b", "remote-b")], expiresAt: new Date(Date.now() + 300000).toISOString() };
    else if (path === "/api/billing/access") result = unavailableQuota ? { message: "Temporarily unavailable" } : access(nextUsage);
    else if (path.endsWith("/devices/selection") && init.method === "POST") { nextUsage++; result = { id: "bound-a", instanceId: configuration.instance.id, kind: "socket", name: "Provider plug", remoteId: "remote-a", channel: "0", isDefault: false, displayName: body.displayName ?? null }; if (holdSelection) await new Promise<void>(resolve => { releaseSelection = resolve; }); }
    else if (path.endsWith("/devices")) result = existing;
    else throw new Error(`Unexpected path ${path}`);
    return { status: unavailableQuota && path === "/api/billing/access" ? 503 : 200, ok: !(unavailableQuota && path === "/api/billing/access"), text: async () => JSON.stringify(result) };
  } });
  return { api: new DeyeSolarApi(client), client, configuration, calls, release: () => releaseSelection?.() };
}
async function mount(f: ReturnType<typeof fixture>) {
  globals.IS_REACT_ACT_ENVIRONMENT = true; globals.__smartUi = { auth: { api: f.api, apiBaseUrl: "https://solar.example", isDemo: false }, route: { params: { kind: "socket", instanceId: f.configuration.instance.id } } };
  const Connect = await component(); let renderer!: ReturnType<typeof create>;
  await act(async () => { renderer = create(React.createElement(Connect)); });
  await act(async () => { renderer.root.findAllByType("button").find(node => node.props.label === "Choose devices")!.props.onPress(); }); return renderer;
}
async function finish(renderer?: ReturnType<typeof create>) { if (renderer) await act(async () => renderer.unmount()); delete globals.__smartUi; delete globals.IS_REACT_ACT_ENVIRONMENT; }
test("real Connect shows canonical unsupported/unknown facts and saves the edited name atomically before quota refresh", async () => {
  const f = fixture(); let renderer: ReturnType<typeof create> | undefined;
  try {
    renderer = await mount(f);
    assert.match(JSON.stringify(renderer.toJSON()), /This device reports readings but cannot be switched/);
    assert.equal(renderer.root.findAllByType("StatusPill").filter(node => node.props.label === "Status unknown").length, 2);
    assert.equal(renderer.root.findAllByType("StatusPill").some(node => node.props.label === "Online"), false);
    await act(async () => { renderer!.root.findAllByType("TextField").filter(node => node.props.label === "Name in SmartSolar")[0]!.props.onChangeText(" Kitchen heater "); });
    await act(async () => { renderer!.root.findAllByType("button").find(node => node.props.label === "Add device")!.props.onPress(); });
    const select = f.calls.find(call => call.path.endsWith("/devices/selection") && call.method === "POST")!;
    assert.equal(select.body.displayName, "Kitchen heater"); assert.equal(select.body.selectionToken, "proof-a"); assert.equal(select.body.draft.expectedRevision, 7);
    assert.equal(f.calls.some(call => call.path.endsWith("/name") || call.path.endsWith("/state")), false);
    assert.ok(renderer.root.findAllByType("StatusPill").some(node => node.props.label === "Premium"));
    const extra = renderer.root.findAllByType("button").find(node => node.props.label === "Add device")!; assert.equal(extra.props.disabled, true);
    await act(async () => { extra.props.onPress(); }); assert.equal(f.calls.filter(call => call.path.endsWith("/devices/selection") && call.method === "POST").length, 1);
    assert.equal(renderer.root.findAllByType("DataRow").find(node => node.props.label === "Smart plugs")!.props.value, "1 of 1");
  } finally { await finish(renderer); }
});
test("existing binding remains Connected while trial extras require Premium, and unavailable quota blocks adding", async () => {
  const existing: IntegrationDeviceBinding = { id: "existing-a", instanceId: "instance-a", kind: "socket", name: "Provider plug", displayName: "Kitchen plug", remoteId: "remote-a", channel: "0", isDefault: false };
  for (const unavailable of [false, true]) {
    const f = unavailable ? fixture(0, [], true) : fixture(1, [existing]); let renderer: ReturnType<typeof create> | undefined;
    try {
      renderer = await mount(f);
      if (!unavailable) { assert.ok(renderer.root.findAllByType("StatusPill").some(node => node.props.label === "Connected")); assert.ok(renderer.root.findAllByType("StatusPill").some(node => node.props.label === "Premium")); assert.match(JSON.stringify(renderer.toJSON()), /Kitchen plug/); }
      const buttons = renderer.root.findAllByType("button").filter(node => node.props.label === "Add device"); assert.ok(buttons.length); assert.ok(buttons.every(node => node.props.disabled));
      await act(async () => { buttons[0]!.props.onPress(); }); assert.equal(f.calls.some(call => call.path.endsWith("/devices/selection") && call.method === "POST"), false);
    } finally { await finish(renderer); }
  }
});

test("a late selection response and captured callback cannot publish a previous account name or quota", async () => {
  const previous = fixture(0, [], false, true), next = fixture(); let renderer: ReturnType<typeof create> | undefined; let pending: Promise<unknown> | undefined;
  try {
    renderer = await mount(previous); const Connect = renderer.root.type;
    await act(async () => { renderer!.root.findAllByType("TextField").filter(node => node.props.label === "Name in SmartSolar")[0]!.props.onChangeText("Previous account label"); });
    const callback = renderer.root.findAllByType("button").find(node => node.props.label === "Add device")!.props.onPress;
    await act(async () => { pending = callback(); });
    assert.equal(previous.calls.filter(call => call.path.endsWith("/devices/selection")).length, 1);
    await act(async () => { globals.__smartUi!.auth = { api: next.api, apiBaseUrl: "https://solar.example", isDemo: false }; renderer!.update(React.createElement(Connect)); });
    await act(async () => { previous.release(); await pending; await callback(); });
    assert.equal(next.calls.some(call => call.path.endsWith("/devices/selection")), false);
    assert.equal(renderer.root.findAllByType("StatusPill").some(node => /Device connected/.test(node.props.label)), false);
    assert.equal(renderer.root.findAllByType("TextField").some(node => node.props.value === "Previous account label"), false);
  } finally { previous.release(); await finish(renderer); }
});
