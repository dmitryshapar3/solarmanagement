import assert from "node:assert/strict";
import { test } from "node:test";
import { readFileSync } from "node:fs";
import { createRequire } from "node:module";
import path from "node:path";
import { build } from "esbuild";
import { ApiClient } from "../src/core/api/ApiClient";
import { IntegrationApi, type IntegrationOAuthStart, type IntegrationOAuthStatus } from "../src/core/api/IntegrationApi";
import { applyIntegrationOAuth, createIntegrationDraft, integrationChange, integrationDraftChanged } from "../src/features/integrations/integrationDraft";
import { authorizeIntegration, validIntegrationOAuthCallback, type IntegrationOAuthBrowser } from "../src/features/integrations/integrationOAuth";
import { integrationFixture } from "./support/integrationFixture";

const start: IntegrationOAuthStart = { flowId: "11111111-1111-4111-8111-111111111111", returnNonce: "nonce-for-this-flow",
  authorizationUrl: "https://identity.example/authorize?state=server-managed", returnUri: "deyesolar://integration-oauth", expiresAt: "2099-01-01T00:00:00Z" };
const callback = `${start.returnUri}?flowId=${start.flowId}&returnNonce=${start.returnNonce}`;
const ready: IntegrationOAuthStatus = { flowId: start.flowId, status: "ready", expiresAt: start.expiresAt,
  values: { region: "authorized-account" }, secretPresent: { token: true } };

function fixture(status = ready, request = start) {
  const { provider, configuration } = integrationFixture();
  provider.actions.push("oauth");
  provider.requiredUiFeatures.push("oauth");
  provider.oauthDefinition = { secretFieldKeys: ["token"] };
  configuration.secretPresent.token = false;
  const draft = createIntegrationDraft(provider, configuration);
  const calls: { route: string; method: string; body: any; token?: string }[] = [];
  const client = new ApiClient({ baseUrl: "https://solar.example", token: "account-one", transport: async (url, init) => {
    const route = new URL(url).pathname;
    calls.push({ route, method: init.method, body: init.body ? JSON.parse(init.body) : undefined, token: init.headers.Authorization });
    const result = route.endsWith("/start") ? request : route.endsWith("/cancel") ? { ...status, status: "cancelled" } : status;
    return { ok: true, status: 200, text: async () => JSON.stringify(result) };
  } });
  return { api: new IntegrationApi(client), client, calls, draft };
}

test("OAuth uses authenticated server status and opaque draft intent; explicit save is required and tokens are never echoed", async () => {
  const { api, calls, draft } = fixture();
  const browser: IntegrationOAuthBrowser = { open: async (url, uri) => {
    assert.equal(url, start.authorizationUrl); assert.equal(uri, start.returnUri);
    return { type: "success", url: callback };
  }, dismiss() { assert.fail("Completed browser must not be dismissed."); } };
  const status = await authorizeIntegration(api, "instance-a", integrationChange(draft, { allowMissingOAuthSecrets: true }), new AbortController().signal, browser);
  const authorized = applyIntegrationOAuth(draft, status!);
  assert.equal(authorized.values.region, "authorized-account");
  assert.equal(authorized.configuration.values.region, "eu");
  assert.equal(authorized.configuration.secretPresent.token, false);
  assert.equal(integrationDraftChanged(authorized), true);
  assert.equal(integrationChange(authorized).oauthFlowId, start.flowId);
  assert.deepEqual(integrationChange(authorized).secretOperations, { token: { operation: "keep" } });
  assert.deepEqual(calls.map(call => [call.method, call.route]), [["POST", "/api/v2/integrations/instance-a/oauth/start"], ["GET", `/api/v2/integrations/instance-a/oauth/${start.flowId}`]]);
  assert.equal(calls[0]!.body.client, "mobile");
  assert.equal(calls.some(call => call.method === "PUT"), false);
  assert.equal(JSON.stringify(authorized).includes("server-managed"), false);
  const saved = createIntegrationDraft(draft.provider, { ...draft.configuration, values: { ...draft.configuration.values, region: "authorized-account" }, secretPresent: { token: true } });
  assert.equal(integrationChange(saved).oauthFlowId, undefined);
  assert.equal(integrationDraftChanged(saved), false);
  assert.throws(() => applyIntegrationOAuth(draft, { ...ready, values: { token: "must-never-enter-public-draft" } }), /unsupported public settings/);
});

const forgedCallbacks = [
  callback.replace("nonce-for-this-flow", "forged"), callback.replace(start.flowId, "another-flow"),
  callback.replace("integration-oauth?", "integration-oauth/other?"), callback.replace("deyesolar:", "https:"),
  `${callback}&returnNonce=${start.returnNonce}`, `${callback}&flowId=${start.flowId}`, `${callback}&token=leak`,
  `${callback}#fragment`, `${callback}#`, callback.replace("integration-oauth?", "attacker@integration-oauth?"),
  callback.replace("integration-oauth?", "integration-oauth:123?"), "not-a-url"
];
for (const [index, url] of forgedCallbacks.entries()) test(`invalid OAuth callback ${index + 1} cannot read a result or mutate settings`, async () => {
  const { api, draft, calls } = fixture();
  assert.equal(validIntegrationOAuthCallback(url, start), false);
  await assert.rejects(authorizeIntegration(api, "instance-a", integrationChange(draft, { allowMissingOAuthSecrets: true }), new AbortController().signal,
    { open: async () => ({ type: "success", url }), dismiss() {} }), /invalid callback/);
  assert.equal(calls.filter(call => call.method === "GET").length, 0);
  assert.equal(calls.filter(call => call.route.endsWith("/cancel")).length, 1);
  assert.equal(draft.configuration.secretPresent.token, false);
  assert.equal(draft.values.region, "eu");
});

test("account replacement aborts an open OAuth browser and ignores its late callback without using new account credentials", async () => {
  const { api, client, draft, calls } = fixture();
  let complete!: (value: { type: string; url: string }) => void;
  let dismissals = 0;
  let opened!: () => void;
  const open = new Promise<void>(resolve => { opened = resolve; });
  const pending = authorizeIntegration(api, "instance-a", integrationChange(draft, { allowMissingOAuthSecrets: true }), new AbortController().signal,
    { open: () => { opened(); return new Promise(resolve => { complete = resolve; }); }, dismiss: () => { ++dismissals; } });
  await open;
  const canceled = assert.rejects(pending, { name: "AbortError" });
  client.setToken("account-two");
  await canceled;
  complete({ type: "success", url: callback });
  await Promise.resolve();
  assert.equal(dismissals, 1);
  assert.equal(calls.length, 1);
  assert.equal(calls[0]!.token, "Bearer account-one");
  assert.equal(draft.oauth, undefined);
});

test("explicit browser cancellation cancels the durable flow once and never saves", async () => {
  const { api, draft, calls } = fixture();
  const result = await authorizeIntegration(api, "instance-a", integrationChange(draft, { allowMissingOAuthSecrets: true }), new AbortController().signal,
    { open: async () => ({ type: "cancel" }), dismiss() {} });
  assert.equal(result, null);
  assert.equal(calls.filter(call => call.route.endsWith("/cancel")).length, 1);
  assert.equal(calls.some(call => call.method === "PUT" || call.method === "GET"), false);
});

test("a browser that never returns is dismissed when the server flow expires and busy work terminates", async () => {
  const { api, draft, calls } = fixture(ready, { ...start, expiresAt: new Date(Date.now() + 25).toISOString() });
  let dismissals = 0;
  await assert.rejects(authorizeIntegration(api, "instance-a", integrationChange(draft, { allowMissingOAuthSecrets: true }), new AbortController().signal,
    { open: async () => new Promise(() => {}), dismiss() { ++dismissals; } }), /request expired/);
  assert.equal(dismissals, 1);
  assert.equal(calls.filter(call => call.route.endsWith("/cancel")).length, 1);
  assert.equal(calls.some(call => call.method === "GET" || call.method === "PUT"), false);
});

test("unsafe or expired start requests never open an external authorization browser", async () => {
  for (const request of [
    { ...start, authorizationUrl: "http://identity.example/authorize" },
    { ...start, authorizationUrl: "https://attacker@identity.example/authorize" },
    { ...start, authorizationUrl: `${start.authorizationUrl}#` },
    { ...start, returnUri: "deyesolar://different-return" },
    { ...start, expiresAt: "2000-01-01T00:00:00Z" }
  ]) {
    const { api, draft, calls } = fixture(ready, request);
    await assert.rejects(authorizeIntegration(api, "instance-a", integrationChange(draft, { allowMissingOAuthSecrets: true }), new AbortController().signal,
      { open: async () => { assert.fail("Unsafe browser request must not open."); }, dismiss() {} }), /unsafe or expired/);
    assert.equal(calls.filter(call => call.route.endsWith("/cancel")).length, 1);
    assert.equal(draft.oauth, undefined);
  }
});

test("the total OAuth result budget aborts a stalled authenticated read instead of multiplying per-request timeouts", async t => {
  const realTimeout = globalThis.setTimeout;
  t.mock.method(globalThis, "setTimeout", (handler: (...args: any[]) => void, delay?: number, ...args: any[]) =>
    realTimeout(handler, delay === 10000 ? 5 : delay, ...args));
  const { draft } = fixture();
  let readSignal: AbortSignal | undefined;
  let cancels = 0;
  const api = new IntegrationApi(new ApiClient({ baseUrl: "https://solar.example", transport: async (url, init) => {
    if (url.endsWith("/cancel")) { ++cancels; return { ok: true, status: 200, text: async () => "{}" }; }
    if (url.endsWith("/start")) return { ok: true, status: 200, text: async () => JSON.stringify(start) };
    readSignal = init.signal;
    return new Promise(() => {});
  } }));
  await assert.rejects(authorizeIntegration(api, "instance-a", integrationChange(draft, { allowMissingOAuthSecrets: true }), new AbortController().signal,
    { open: async () => ({ type: "success", url: callback }), dismiss() { assert.fail("Result reads do not own an open browser."); } }), /result took too long/);
  assert.equal(readSignal!.aborted, true);
  assert.equal(cancels, 1);
  assert.equal(draft.oauth, undefined);
});

test("a stalled cancellation cleanup cannot retain local OAuth busy ownership", async () => {
  const { draft } = fixture();
  let cancellationStarted = false;
  const client = new ApiClient({ baseUrl: "https://solar.example", token: "original-owner", transport: async (url) => {
    if (url.endsWith("/start")) return { ok: true, status: 200, text: async () => JSON.stringify(start) };
    cancellationStarted = true;
    return new Promise(() => {});
  } });
  const result = await authorizeIntegration(new IntegrationApi(client), "instance-a", integrationChange(draft, { allowMissingOAuthSecrets: true }), new AbortController().signal,
    { open: async () => ({ type: "cancel" }), dismiss() {} });
  assert.equal(result, null);
  assert.equal(cancellationStarted, true);
  client.setToken("replacement-owner");
  await Promise.resolve();
  assert.equal(draft.oauth, undefined);
});

test("local abort releases the browser gate; an old completion cannot dismiss or authorize a subsequent flow", async () => {
  const { api, draft, calls } = fixture();
  const change = integrationChange(draft, { allowMissingOAuthSecrets: true });
  let finishOld!: (result: { type: string; url: string }) => void;
  let opened!: () => void;
  const openedPromise = new Promise<void>(resolve => { opened = resolve; });
  let dismissals = 0;
  const controller = new AbortController();
  const pending = authorizeIntegration(api, "instance-a", change, controller.signal, {
    open: () => { opened(); return new Promise(resolve => { finishOld = resolve; }); }, dismiss() { ++dismissals; }
  });
  await openedPromise;
  await assert.rejects(authorizeIntegration(api, "instance-b", change, new AbortController().signal, { open: async () => ({ type: "cancel" }), dismiss() {} }), /already open/);
  const canceled = assert.rejects(pending, { name: "AbortError" });
  controller.abort();
  await canceled;
  const next = await authorizeIntegration(api, "instance-a", change, new AbortController().signal, {
    open: async () => { finishOld({ type: "success", url: callback }); return { type: "success", url: callback }; }, dismiss() { ++dismissals; }
  });
  assert.equal(next!.status, "ready");
  assert.equal(dismissals, 1);
  assert.equal(calls.filter(call => call.method === "GET").length, 1);
});

test("replayed consumed flows and mismatched authoritative flow identities cannot become ready drafts", async () => {
  for (const status of [{ ...ready, status: "consumed" }, { ...ready, flowId: "wrong-flow" }]) {
    const { api, draft, calls } = fixture(status);
    await assert.rejects(authorizeIntegration(api, "instance-a", integrationChange(draft, { allowMissingOAuthSecrets: true }), new AbortController().signal,
      { open: async () => ({ type: "success", url: callback }), dismiss() {} }), /failed or expired|different flow/);
    assert.equal(draft.oauth, undefined);
    assert.equal(calls.some(call => call.method === "PUT"), false);
  }
});

test("OAuth start bypasses only its declared managed secrets; other required fields remain authoritative", () => {
  const { draft } = fixture();
  draft.values.region = "";
  assert.throws(() => integrationChange(draft, { allowMissingOAuthSecrets: true }), /Region is required/);
  draft.values.region = "eu";
  draft.provider.fields.push({ key: "password", kind: "secret", label: "Password", secret: true, required: true });
  assert.throws(() => integrationChange(draft, { allowMissingOAuthSecrets: true }), /Password is required/);
});

test("ready OAuth replaces only its managed secret intent with keep and preserves unrelated secret changes", () => {
  for (const operation of ["clear", "replace"] as const) {
    const { draft } = fixture();
    draft.secrets.token = { operation, ...(operation === "replace" ? { value: "old-manual-value" } : {}) };
    draft.provider.fields.push({ key: "otherSecret", kind: "secret", label: "Other secret", secret: true, required: true });
    draft.secrets.otherSecret = { operation: "replace", value: "independent-value" };
    const authorized = applyIntegrationOAuth(draft, ready);
    assert.deepEqual(integrationChange(authorized).secretOperations, {
      token: { operation: "keep" }, otherSecret: { operation: "replace", value: "independent-value" }
    });
    assert.equal(integrationChange(authorized).oauthFlowId, ready.flowId);
    assert.equal(JSON.stringify(authorized).includes("old-manual-value"), false);
    assert.equal(draft.secrets.token!.operation, operation);
    assert.equal(draft.secrets.otherSecret!.value, "independent-value");
  }
});

test("the forged callback regression detects removal of nonce fencing in an isolated in-memory mutation", async () => {
  const sourcePath = path.join(process.cwd(), "src/features/integrations/integrationOAuth.ts");
  const source = readFileSync(sourcePath, "utf8");
  const guard = 'callback.searchParams.get("returnNonce") === start.returnNonce';
  assert.equal(source.split(guard).length, 2);
  const bundle = await build({ stdin: { contents: source.replace(guard, "true"), resolveDir: path.dirname(sourcePath), loader: "ts" },
    bundle: true, write: false, platform: "node", format: "cjs", external: ["expo-web-browser"] });
  const module = { exports: {} as { authorizeIntegration: typeof authorizeIntegration } };
  new Function("require", "module", "exports", bundle.outputFiles[0]!.text)(createRequire(path.join(process.cwd(), "package.json")), module, module.exports);
  const { api, draft, calls } = fixture();
  const changed = integrationChange(draft, { allowMissingOAuthSecrets: true });
  const forgedBrowser: IntegrationOAuthBrowser = { open: async () => ({ type: "success", url: callback.replace(start.returnNonce, "foreign-nonce") }), dismiss() {} };
  await assert.rejects(authorizeIntegration(api, "instance-a", changed, new AbortController().signal, forgedBrowser), /invalid callback/);
  assert.equal(calls.filter(call => call.method === "GET").length, 0);
  const incorrectlyAccepted = await module.exports.authorizeIntegration(api, "instance-a", changed, new AbortController().signal, forgedBrowser);
  assert.equal(incorrectlyAccepted!.status, "ready");
  assert.equal(calls.filter(call => call.method === "GET").length, 1);
});
