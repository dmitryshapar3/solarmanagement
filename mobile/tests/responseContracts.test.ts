import assert from "node:assert/strict";
import { test } from "node:test";
import { ApiClient, ApiError } from "../src/core/api/ApiClient";
import { DeyeSolarApi } from "../src/core/api/DeyeSolarApi";
import { validApiResponse } from "../src/core/api/responseContracts";
import { demoInverter, demoReadings, createDemoState } from "../src/features/demo/fixtures";
import { integrationFixture } from "./support/integrationFixture";
const invalidMessage = "The server returned an invalid API response. Check the server URL and try again.";
test("rule-run metrics require explicit numbers or null and preserve measured zero", () => {
  const run = { id: 1, timestamp: "2026-10-05T11:00:00Z", ruleName: "Rule", action: "NO_CHANGE", conditionKey: "unknown", reason: "No telemetry", batterySoc: null, solarProduction: null, batteryPower: null };
  assert.equal(validApiResponse("/api/rule-runs", "GET", [run]), true);
  for (const key of ["batterySoc", "solarProduction", "batteryPower"]) {
    assert.equal(validApiResponse("/api/rule-runs", "GET", [{ ...run, [key]: 0 }]), true, key);
    for (const invalid of [undefined, "0", Number.POSITIVE_INFINITY])
      assert.equal(validApiResponse("/api/rule-runs", "GET", [{ ...run, [key]: invalid }]), false, key);
  }
});
test("historical measurements require point-in-time quality independently of numeric zero", () => {
  const [reading] = demoReadings(1, new Date("2026-10-05T12:00:00Z"), "UTC");
  assert.equal(validApiResponse("/api/readings", "GET", [reading]), true);
  for (const key of ["batterySocValid", "batteryPowerValid", "batteryTemperatureValid", "batteryVoltageValid", "batteryCurrentValid", "loadPowerValid", "gridPowerValid", "solarPowerValid"]) {
    assert.equal(validApiResponse("/api/readings", "GET", [{ ...reading, [key]: false }]), true, key);
    for (const invalid of [undefined, null, "false"])
      assert.equal(validApiResponse("/api/readings", "GET", [{ ...reading, [key]: invalid }]), false, key);
  }
});
for (const [path, payload] of [
  ["/api/auth/login", { token: 7, username: "owner", expiresAt: "2026-11-04T12:00:00Z" }],
  ["/api/auth/session", { authenticated: "true", username: "owner" }],
  ["/api/auth/identities", { email: null, phone: null, googleLinked: "true" }],
  ["/api/devices", { devices: [{ id: "socket", name: "Socket", category: null, online: true, isOn: "false", currentPowerW: 0 }], lastUpdated: null }],
  ["/api/rules", {}],
  ["/api/settings", { polling: { intervalSeconds: "10" }, display: { timeZoneId: "UTC" } }],
  ["/api/v2/devices/socket/commands/command", { commandId: "command", deviceId: "socket", isOn: true, status: "future-success", rejection: null, createdAt: "2026-10-04T12:00:00Z", completedAt: null }],
  ["/api/v2/integration-providers", { providers: [null], revision: "7" }]
] as const) {
  test(`malformed successful ${path} cannot enter feature state or expire its valid session`, async () => {
    let expired = 0;
    const client = new ApiClient({ baseUrl: "https://solar.example", token: "current", onUnauthorized: () => expired++,
      transport: async () => ({ status: 200, ok: true, text: async () => JSON.stringify(payload) }) });
    await assert.rejects(client.request(path), error => error instanceof ApiError && error.message === invalidMessage);
    assert.equal(expired, 0);
  });
}
test("linked identity status requires explicit verified contacts and a boolean Google binding", () => {
  const status = { email: "owner@example.test", phone: "+48123456789", googleLinked: true };
  assert.equal(validApiResponse("/api/auth/identities", "GET", status), true);
  assert.equal(validApiResponse("/api/auth/identities", "GET", { email: null, phone: null, googleLinked: false }), true);
  for (const field of ["email", "phone", "googleLinked"])
    assert.equal(validApiResponse("/api/auth/identities", "GET", { ...status, [field]: undefined }), false, field);
});
test("malformed token and expiry cannot reach successful sign-in persistence", async () => {
  const api = new DeyeSolarApi(new ApiClient({ baseUrl: "https://solar.example", transport: async () => ({ status: 200, ok: true,
    text: async () => JSON.stringify({ token: "otherwise-valid", username: "owner", expiresAt: "never" }) }) }));
  await assert.rejects(api.login("owner", "password"), { message: invalidMessage });
});
test("explicit device state is required while additional provider fields remain extensible", () => {
  const { provider } = integrationFixture();
  assert.equal(validApiResponse("/api/v2/integration-providers", "GET", { providers: [{ ...provider, futureOptionalField: "opaque" }], revision: "fixture" }), true);
  const device = { id: "socket", name: "Socket", category: null, online: true, isOn: false, stateKnown: true, currentPowerW: null };
  assert.equal(validApiResponse("/api/devices", "GET", { devices: [device], lastUpdated: null }), true);
  assert.equal(validApiResponse("/api/devices", "GET", { devices: [{ ...device, stateKnown: false }], lastUpdated: null }), true);
  assert.equal(validApiResponse("/api/devices", "GET", { devices: [{ ...device, currentPowerW: Number.POSITIVE_INFINITY }], lastUpdated: null }), false);
});


test("device responses reject omitted or nullable state knowledge instead of assuming OFF", () => {
  const device = { id: "socket", name: "Socket", category: null, online: true, isOn: false, currentPowerW: null };
  for (const stateKnown of [undefined, null, "true"])
    assert.equal(validApiResponse("/api/devices", "GET", { devices: [{ ...device, stateKnown }], lastUpdated: null }), false);
});


test("current dashboard requires explicit measurement validity and source metadata keys", () => {
  const now = new Date("2026-10-05T12:00:00Z");
  const inverter = demoInverter(now, "UTC");
  const dashboard = { inverter, devicesLoaded: true, deviceLastUpdated: null, devices: [], manualDevices: [], rules: [], timeZoneId: "UTC" };
  assert.equal(validApiResponse("/api/dashboard", "GET", dashboard), true);
  for (const key of ["batterySocValid", "batteryPowerValid", "batteryTemperatureValid", "batteryVoltageValid", "batteryCurrentValid", "loadPowerValid", "gridPowerValid", "solarPowerValid"])
    for (const value of [undefined, null])
      assert.equal(validApiResponse("/api/dashboard", "GET", { ...dashboard, inverter: { ...inverter, [key]: value } }), false, key);
  for (const key of ["inverterId", "solarObservedAt", "gridObservedAt", "solarDeviceSn", "gridDeviceSn"])
    assert.equal(validApiResponse("/api/dashboard", "GET", { ...dashboard, inverter: { ...inverter, [key]: undefined } }), false, key);
  assert.equal(validApiResponse("/api/dashboard", "GET", { ...dashboard, inverter: { ...inverter, solarPowerValid: false, solarObservedAt: null, solarDeviceSn: null } }), true);
});

test("rule responses require a current configuration token", () => {
  const rule = createDemoState(new Date("2026-10-05T12:00:00Z")).rules[0]!;
  assert.equal(validApiResponse("/api/rules", "GET", [rule]), true);
  for (const configurationVersion of [undefined, null, "", "bad-version", "z".repeat(64)])
    assert.equal(validApiResponse("/api/rules", "GET", [{ ...rule, configurationVersion }]), false);
});

test("rule toggle and delete forward the configuration token read by the view", async () => {
  const rule = createDemoState(new Date("2026-10-05T12:00:00Z")).rules[0]!;
  const sent: Array<{ method: string; body?: string; headers: Record<string, string> }> = [];
  const api = new DeyeSolarApi(new ApiClient({ baseUrl: "https://solar.example", transport: async (_url, init) => {
    sent.push(init);
    return { status: init.method === "DELETE" ? 204 : 200, ok: true, text: async () => init.method === "DELETE" ? "" : JSON.stringify(rule) };
  } }));
  await api.setRuleEnabled(rule.id, false, rule.configurationVersion);
  assert.deepEqual(JSON.parse(sent[0]!.body!), { enabled: false, configurationVersion: rule.configurationVersion });
  await api.deleteRule(rule.id, rule.configurationVersion);
  assert.equal(sent[1]!.headers["If-Match"], `"${rule.configurationVersion}"`);
});

test("history read cancellation reaches the real transport and preserves selection parameters", async () => {
  for (const kind of ["readings", "runs"]) {
    const controller = new AbortController();
    let sentUrl = "", sentSignal: AbortSignal | undefined;
    let complete!: (response: { status: number; ok: boolean; text: () => Promise<string> }) => void;
    const api = new DeyeSolarApi(new ApiClient({ baseUrl: "https://solar.example", transport: async (url, init) => {
      sentUrl = url; sentSignal = init.signal;
      return new Promise(resolve => { complete = resolve; });
    } }));
    const pending = kind === "readings" ? api.getReadings(24, controller.signal) : api.getRuleRuns(6, "CHANGES", controller.signal);
    if (kind === "readings") assert.ok(sentUrl.endsWith("/api/readings?hours=24"));
    else assert.ok(sentUrl.includes("hours=6") && sentUrl.includes("filter=CHANGES"));
    controller.abort();
    assert.equal(sentSignal?.aborted, true);
    await assert.rejects(pending, { name: "AbortError" });
    complete({ status: 200, ok: true, text: async () => "[]" });
  }
});
