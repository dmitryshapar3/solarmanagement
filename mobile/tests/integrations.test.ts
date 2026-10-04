import assert from "node:assert/strict";
import { test } from "node:test";
import { ApiClient, ApiError } from "../src/core/api/ApiClient";
import { IntegrationApi } from "../src/core/api/IntegrationApi";
import { integrationFixture } from "./support/integrationFixture";
import { createIntegrationDraft, integrationChange, integrationDraftChanged, unsupportedProvider } from "../src/features/integrations/integrationDraft";


test("generic drafts never echo saved secrets and retain explicit version and secret intent", () => {
  const { provider, configuration } = integrationFixture();
  configuration.values.token = "accidentally-echoed-secret";
  const draft = createIntegrationDraft(provider, configuration);
  assert.equal(JSON.stringify(draft).includes("accidentally-echoed-secret"), false);
  assert.equal(integrationDraftChanged(draft), false);
  assert.deepEqual(integrationChange(draft), {
    expectedRevision: 7, packageVersion: "1.0.0", packageDigest: "package-a", descriptorDigest: "schema-a",
    values: { region: "eu", interval: 10, mode: "cloud", readOnly: false }, secretOperations: { token: { operation: "keep" } }
  });
  draft.secrets.token = { operation: "replace", value: "new-key" };
  assert.equal(integrationDraftChanged(draft), true);
  assert.deepEqual(integrationChange(draft).secretOperations, { token: { operation: "replace", value: "new-key" } });
  draft.secrets.token = { operation: "clear" };
  assert.throws(() => integrationChange(draft), /API key is required/);
  provider.fields.find(field => field.key === "token")!.required = false;
  assert.deepEqual(integrationChange(draft).secretOperations, { token: { operation: "clear" } });
});

test("empty, fractional, out of range and unavailable values cannot be sent as valid required configuration", () => {
  const { provider, configuration } = integrationFixture();
  const draft = createIntegrationDraft(provider, configuration);
  for (const interval of ["", "5.1", "4", "61", "NaN", "10garbage", "9007199254740993"]) {
    draft.values.interval = interval;
    assert.throws(() => integrationChange(draft), /Interval/);
  }
  draft.values.interval = "60";
  assert.equal(integrationChange(draft).values.interval, 60);
  draft.values.mode = "unpublished-option";
  assert.throws(() => integrationChange(draft), /Mode/);
  draft.values.mode = "cloud";
  draft.configuration.secretPresent.token = false;
  assert.throws(() => integrationChange(draft), /API key is required/);
});

test("unsupported required UI stops mutations while unknown optional saved fields remain intact", () => {
  const { provider, configuration } = integrationFixture();
  provider.fields.push({ key: "custom", label: "Custom control", kind: "future-control", required: true, secret: false, options: [] });
  assert.match(unsupportedProvider(provider)!, /cannot display/);
  assert.throws(() => integrationChange(createIntegrationDraft(provider, configuration)), /cannot display/);
  provider.fields.at(-1)!.required = false;
  configuration.values.custom = "existing-opaque-value";
  assert.equal(integrationChange(createIntegrationDraft(provider, configuration)).values.custom, "existing-opaque-value");
  provider.requiredUiFeatures.push("future-control");
  assert.match(unsupportedProvider(provider)!, /newer app/);
  provider.requiredUiFeatures.pop();
  provider.uiContractVersion = 2;
  assert.match(unsupportedProvider(provider)!, /newer app/);
});

test("unpersisted defaults require a save and an optional cleared select sends null", () => {
  const { provider, configuration } = integrationFixture();
  delete configuration.values.readOnly;
  const draft = createIntegrationDraft(provider, configuration);
  assert.equal(integrationDraftChanged(draft), true);
  provider.fields.find(field => field.key === "mode")!.required = false;
  draft.values.mode = "";
  assert.equal(integrationChange(draft).values.mode, null);
});

test("integration APIs send read-only draft probes with version fencing and opaque selection tokens", async () => {
  const calls: { path: string; method: string; body: unknown; bearer?: string }[] = [];
  const api = new IntegrationApi(new ApiClient({ baseUrl: "https://solar.example", token: "owner-token", transport: async (url, options) => {
    calls.push({ path: new URL(url).pathname, method: options.method, body: options.body ? JSON.parse(options.body) : undefined,
      bearer: options.headers.Authorization });
    return { status: 200, ok: true, text: async () => "{}" };
  } }));
  const { provider, configuration } = integrationFixture();
  const change = integrationChange(createIntegrationDraft(provider, configuration));
  await api.test("one/two:socket", change);
  await api.discover("one/two:socket", change);
  assert.deepEqual(calls, [
    { path: "/api/v2/integrations/one%2Ftwo%3Asocket/test", method: "POST", body: change, bearer: "Bearer owner-token" },
    { path: "/api/v2/integrations/one%2Ftwo%3Asocket/discovery", method: "POST", body: change, bearer: "Bearer owner-token" }
  ]);
  await api.selectDevice("instance-a", change, "opaque-proof-not-a-remote-id");
  assert.deepEqual(calls[2]!.body, { draft: change, selectionToken: "opaque-proof-not-a-remote-id" });
  assert.equal(calls.some(call => call.method === "PUT" || call.path.includes("/state")), false);
});

test("configuration conflicts remain failures and canceled integration responses cannot enter a replacement session", async () => {
  const { provider, configuration } = integrationFixture();
  const change = integrationChange(createIntegrationDraft(provider, configuration));
  const conflict = new IntegrationApi(new ApiClient({ baseUrl: "https://solar.example", transport: async () => ({
    status: 409, ok: false, text: async () => JSON.stringify({ code: "configuration_conflict", message: "A newer revision exists." })
  }) }));
  await assert.rejects(conflict.saveConfiguration("instance-a", change), error => error instanceof ApiError && error.status === 409);
  let complete: ((value: { status: number; ok: boolean; text: () => Promise<string> }) => void) | undefined;
  const client = new ApiClient({ baseUrl: "https://solar.example", token: "old-owner", transport: async () => new Promise(resolve => { complete = resolve; }) });
  const pending = new IntegrationApi(client).getCatalog();
  client.setToken("new-owner");
  await assert.rejects(pending, /canceled/);
  complete!({ status: 200, ok: true, text: async () => JSON.stringify({ providers: [provider], revision: "old-account" }) });
});

test("only provider setup operations receive the extended deadline; inventory and command deadlines stay bounded", async t => {
  const timer = t.mock.method(globalThis, "setTimeout");
  const calls: { path: string; deadline: number }[] = [];
  const api = new IntegrationApi(new ApiClient({ baseUrl: "https://solar.example", transport: async (url) => {
    calls.push({ path: new URL(url).pathname, deadline: timer.mock.calls.at(-1)!.arguments[1] as number });
    return { status: 200, ok: true, text: async () => "{}" };
  } }));
  const { provider, configuration } = integrationFixture();
  const change = integrationChange(createIntegrationDraft(provider, configuration));
  await api.getCatalog();
  await api.test("instance-a", change);
  await api.discover("instance-a", change);
  await api.setEnabled("instance-a", true, change);
  await api.switchPackage("instance-a", change, "2.0.0");
  await api.setEnabled("instance-a", false, change);
  await api.getDevices("instance-a");
  await api.sendDeviceCommand("socket-a", "command-a", true);
  await api.getDeviceCommand("socket-a", "command-a");
  await api.getUnresolvedCommands("socket-a");
  await api.releaseDeviceCommand("socket-a", "command-a");
  assert.deepEqual(calls.map(call => call.deadline), [15000, 330000, 330000, 330000, 330000, 15000, 15000, 25000, 15000, 15000, 15000]);
});

test("the extended transport deadline accepts its upper bound and still aborts a stalled setup immediately on cancellation", async () => {
  let requests = 0;
  let transportSignal: AbortSignal | undefined;
  const client = new ApiClient({ baseUrl: "https://solar.example", transport: async (_url, init) => {
    ++requests;
    transportSignal = init.signal;
    return new Promise(() => {});
  } });
  for (const timeoutMs of [0, -1, 330001, NaN, Infinity]) {
    await assert.rejects(client.request("/api/v2/integrations/instance-a/test", { timeoutMs }), /timeout/);
  }
  assert.equal(requests, 0);
  const controller = new AbortController();
  const pending = client.request("/api/v2/integrations/instance-a/test", { timeoutMs: 330000, signal: controller.signal });
  const canceled = assert.rejects(pending, { name: "AbortError" });
  controller.abort();
  await canceled;
  assert.equal(requests, 1);
  assert.equal(transportSignal!.aborted, true);
});
