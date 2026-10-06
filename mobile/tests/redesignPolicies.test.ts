import assert from "node:assert/strict";
import { test } from "node:test";
import { ApiClient, ApiError } from "../src/core/api/ApiClient";
import { DeyeSolarApi } from "../src/core/api/DeyeSolarApi";
import type { RuleRequest } from "../src/core/api/types";
import { createDemoState, demoProduction, demoInverter } from "../src/features/demo/fixtures";
import { editorRuleError } from "../src/features/rules/RuleEditorPolicy";
import { exportRangeError } from "../src/features/sales/exportPolicy";
import { validApiResponse } from "../src/core/api/responseContracts";
import { uiHarness } from "./support/uiHarness";
const original: RuleRequest = { ...createDemoState(new Date()).rules[0]!, socTurnOnThreshold: 80, socTurnOffThreshold: 80, useSeparateSocTurnOffThreshold: false };
test("new and edited ranges require hysteresis while untouched legacy equal thresholds survive", () => {
  assert.equal(editorRuleError(original, original), null);
  assert.equal(editorRuleError({ ...original, name: "Rename only" }, original), null);
  assert.equal(editorRuleError(original, null)?.field, "socTurnOffThreshold");
  assert.equal(editorRuleError({ ...original, socTurnOffThreshold: 81, useSeparateSocTurnOffThreshold: true }, original)?.field, "socTurnOffThreshold");
  assert.equal(editorRuleError({ ...original, socTurnOffThreshold: 55, useSeparateSocTurnOffThreshold: true }, original), null);
  assert.equal(editorRuleError({ ...original, intervalSeconds: NaN }, original)?.field, "intervalSeconds");
  assert.equal(editorRuleError({ ...original, useSolarProductionThreshold: true, minAverageSolarProductionWatts: 0 }, original)?.field, "minAverageSolarProductionWatts");
});
test("inclusive export ranges validate real dates, leap years, future bounds and 366-day maximum", () => {
  assert.equal(exportRangeError("2024-01-01", "2024-12-31", "2026-10-06"), null);
  assert.ok(exportRangeError("2024-01-01", "2025-01-01", "2026-10-06"));
  assert.ok(exportRangeError("2025-02-29", "2025-03-01", "2026-10-06"));
  assert.ok(exportRangeError("2026-10-07", "2026-10-07", "2026-10-06"));
  assert.ok(exportRangeError("2026-10-06", "2026-10-05", "2026-10-06"));
  assert.equal(exportRangeError("2026-10-06", "2026-10-06", "2026-10-06"), null);
});
test("activity presents request evidence separately from provider confirmation and physical observation", async () => {
  const functions = await uiHarness('export { activityTitle } from "./src/features/activity/ActivityRow";'); const title = functions.activityTitle as unknown as (item: any) => string;
  assert.equal(title({ kind: "command.manual", reasonCode: "requested", state: true }), "Turn-on requested by hand");
  assert.equal(title({ kind: "rule.switched", state: false }), "Automation requested turn-off");
  assert.equal(title({ kind: "command.result", reasonCode: "uncertain", state: null }), "Command result unknown");
  assert.equal(title({ kind: "command.result", reasonCode: "acknowledged", state: true }), "Provider acknowledged command");
  assert.equal(title({ kind: "device.observed", state: true }), "Device state observed");
});
test("reading trends break at unavailable data, source revisions, generations and time gaps", async () => {
  const functions = await uiHarness('export { readingPoints } from "./src/features/readings/ReadingTrend";'); const points = functions.readingPoints as unknown as (rows: any[], metric: string) => any[];
  const row = { id: 1, timestamp: "2026-10-06T10:00:00Z", batterySoc: 0, inverterId: "one", configurationRevision: 1, runtimeGeneration: 1 };
  assert.equal(points([row], "batterySoc")[0].actual, 0);
  for (const change of [{ inverterId: "two" }, { configurationRevision: 2 }, { runtimeGeneration: 2 }, { timestamp: "2026-10-06T10:15:00Z" }]) {
    const result = points([row, { ...row, timestamp: "2026-10-06T10:05:00Z", ...change }], "batterySoc"); assert.equal(result.length, 3); assert.equal(result[1].actual, null);
  }
  assert.equal(points([{ ...row, batterySoc: null }], "batterySoc")[0].actual, null);
});
test("new optional data views preserve old raw contracts and measured zero remains distinct from absent values", () => {
  const now = new Date("2026-10-06T10:25:00Z"); const production = demoProduction("Today", undefined, now, "UTC");
  assert.equal(validApiResponse("/api/solar/production", "GET", production), true);
  assert.equal(validApiResponse("/api/solar/production", "GET", { ...production, hours: [{ ...production.hours[0], actualKw: undefined }] }), false);
  const reading = { ...demoInverter(now, "UTC"), id: 1 };
  assert.equal(validApiResponse("/api/readings", "GET", [reading]), true);
  const details = { start: reading.timestamp, end: reading.timestamp, aggregate: "5m", items: [{ ...reading, configurationRevision: 1, runtimeGeneration: 1, solarProduction: null }], gaps: [], nextCursor: null, partial: true };
  assert.equal(validApiResponse("/api/readings", "GET", details, { view: "details", aggregate: "5m" }), true);
  assert.equal(validApiResponse("/api/readings", "GET", details), false);
  assert.equal(validApiResponse("/api/v2/integration-providers", "GET", { providers: [], revision: "same", providerKinds: { sample: ["inverter", "socket"] } }), true);
  assert.equal(validApiResponse("/api/v2/integration-providers", "GET", { providers: [], revision: "same", providerKinds: { sample: ["invented"] } }), false);
});
function zipResponse(bytes: Uint8Array = Uint8Array.of(0x50, 0x4b, 3, 4), mime = "application/zip") { return { status: 200, ok: true, text: async () => "", headers: { get: () => mime }, arrayBuffer: async () => bytes.buffer }; }
test("ZIP export uses the protected transport with proof and returns verified bytes", async () => {
  let request: any; const api = new DeyeSolarApi(new ApiClient({ baseUrl: "https://solar.example", token: "fixture-token", transport: async (url, init) => { request = { url, ...init }; return zipResponse(); } }));
  assert.deepEqual(await api.exportZip({ externalProofId: "fresh-proof" }), Uint8Array.of(0x50, 0x4b, 3, 4));
  assert.equal(request.headers.Accept, "application/zip"); assert.equal(request.headers.Authorization, "Bearer fixture-token"); assert.equal(request.credentials, "omit"); assert.deepEqual(JSON.parse(request.body), { proof: { externalProofId: "fresh-proof" } });
});
test("ZIP rejects malformed archives and HTML responses without expiring a valid session", async () => {
  for (const response of [zipResponse(Uint8Array.of(0, 1, 2, 3)), zipResponse(Uint8Array.of(0x50, 0x4b, 3)), zipResponse(undefined, "text/html")]) {
    let expired = 0; const api = new DeyeSolarApi(new ApiClient({ baseUrl: "https://solar.example", token: "fixture-token", onUnauthorized: () => expired++, transport: async () => response }));
    await assert.rejects(api.exportZip({ currentPassword: "fixture" }), error => error instanceof ApiError && /invalid account archive/.test(error.message)); assert.equal(expired, 0);
  }
});
test("ZIP late body reads cannot cross a replacement account or canceled view", async () => {
  for (const replace of [true, false]) {
    let finish!: (value: ArrayBuffer) => void; const signal = new AbortController(); const client = new ApiClient({ baseUrl: "https://solar.example", token: "old", transport: async () => ({ ...zipResponse(), arrayBuffer: () => new Promise(resolve => { finish = resolve; }) }) });
    const pending = new DeyeSolarApi(client).exportZip({ currentPassword: "fixture" }, signal.signal); await Promise.resolve();
    if (replace) client.setToken("replacement"); else signal.abort();
    await assert.rejects(pending, { name: "AbortError" }); finish(Uint8Array.of(0x50, 0x4b, 3, 4).buffer); await Promise.resolve();
  }
});
test("failed fresh proof on ZIP export does not sign out the current mobile session", async () => {
  let expired = 0; const api = new DeyeSolarApi(new ApiClient({ baseUrl: "https://solar.example", token: "current", onUnauthorized: () => expired++, transport: async () => ({ status: 401, ok: false, text: async () => JSON.stringify({ message: "Fresh proof required" }) }) }));
  await assert.rejects(api.exportZip({ currentPassword: "wrong" }), error => error instanceof ApiError && error.status === 401); assert.equal(expired, 0);
});
