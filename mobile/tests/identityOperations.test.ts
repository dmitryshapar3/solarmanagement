import assert from "node:assert/strict";
import { test } from "node:test";
import { ApiClient, ApiError, type ApiTransport } from "../src/core/api/ApiClient";
import { DeyeSolarApi } from "../src/core/api/DeyeSolarApi";
import { SessionOperations } from "../src/application/sessionStorage";
import { linkGoogleIdentity, VerificationRequests } from "../src/features/auth/identityOperations";

const baseUrl = "https://solar.example";
const proof = { code: "one-time-link-ticket", codeVerifier: "original-pkce-proof" };
const challenge = (id: string) => ({ verificationId: id, expiresAt: "2026-10-04T12:10:00Z", retryAfterSeconds: 60 });
const response = (status: number, body: unknown) => ({ status, ok: status >= 200 && status < 300, text: async () => JSON.stringify(body) });
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(done => { resolve = done; });
  return { promise, resolve };
}

test("Google linking exchanges proof with the original bearer and keeps that session", async () => {
  const calls: { url: string; authorization?: string; body?: string }[] = [];
  const client = new ApiClient({ baseUrl, token: "original-account", transport: async (url, init) => {
    calls.push({ url, authorization: init.headers.Authorization, body: init.body });
    return response(200, { token: "unused-new-session", username: "same-owner" });
  } });
  const api = new DeyeSolarApi(client);
  const sessions = new SessionOperations();
  assert.equal(await linkGoogleIdentity(api, sessions.capture(), async () => proof), true);
  await api.getSession();
  assert.deepEqual(calls, [
    { url: `${baseUrl}/api/auth/google/exchange`, authorization: "Bearer original-account", body: JSON.stringify(proof) },
    { url: `${baseUrl}/api/auth/session`, authorization: "Bearer original-account", body: undefined }
  ]);
  assert.equal(sessions.capture().aborted, false);
});

for (const status of [401, 409, 503]) {
  test(`Google link failure ${status} preserves the initiating account for retry`, async () => {
    let expired = 0;
    let linking = true;
    const authorizations: (string | undefined)[] = [];
    const client = new ApiClient({ baseUrl, token: "original-account", onUnauthorized: () => { expired++; }, transport: async (_url, init) => {
      authorizations.push(init.headers.Authorization);
      return linking ? response(status, { message: "The identity could not be linked." }) : response(200, { authenticated: true });
    } });
    const api = new DeyeSolarApi(client);
    await assert.rejects(linkGoogleIdentity(api, new SessionOperations().capture(), async () => proof),
      (error: unknown) => error instanceof ApiError && error.status === status);
    linking = false;
    await api.getSession();
    assert.deepEqual(authorizations, ["Bearer original-account", "Bearer original-account"]);
    assert.equal(expired, 0);
  });
}

test("canceling Google's browser leaves the current account and sends no exchange", async () => {
  let calls = 0;
  const api = new DeyeSolarApi(new ApiClient({ baseUrl, token: "original-account", transport: async () => { calls++; return response(200, {}); } }));
  assert.equal(await linkGoogleIdentity(api, new SessionOperations().capture(), async () => null), false);
  assert.equal(calls, 0);
});

for (const action of ["logout", "replacement-account", "replacement-endpoint"] as const) {
  test(`Google callback after ${action} cannot exchange with or replace the next session`, async () => {
    const browser = deferred<typeof proof>();
    const calls: { url: string; authorization?: string }[] = [];
    const client = new ApiClient({ baseUrl, token: "original-account", transport: async (url, init) => {
      calls.push({ url, authorization: init.headers.Authorization });
      return response(200, { authenticated: true });
    } });
    const api = new DeyeSolarApi(client);
    const sessions = new SessionOperations();
    const work = linkGoogleIdentity(api, sessions.capture(), () => browser.promise);
    const rejected = assert.rejects(work, { name: "AbortError" });
    sessions.begin();
    if (action === "replacement-endpoint") client.setBaseUrl("https://another.example");
    client.setToken(action === "logout" ? null : "next-account");
    browser.resolve(proof);
    await rejected;
    await api.getSession();
    assert.deepEqual(calls, [{
      url: `${action === "replacement-endpoint" ? "https://another.example" : baseUrl}/api/auth/session`,
      authorization: action === "logout" ? undefined : "Bearer next-account"
    }]);
  });
}

test("logout during Google exchange cancels the request and discards its late success", async () => {
  const server = deferred<Awaited<ReturnType<ApiTransport>>>();
  const started = deferred<void>();
  let requestSignal: AbortSignal | undefined;
  const client = new ApiClient({ baseUrl, token: "original-account", transport: async (_url, init) => {
    requestSignal = init.signal; started.resolve(); return server.promise;
  } });
  const sessions = new SessionOperations();
  const work = linkGoogleIdentity(new DeyeSolarApi(client), sessions.capture(), async () => proof);
  const rejected = assert.rejects(work, { name: "AbortError" });
  await started.promise;
  sessions.begin(); client.setToken(null);
  await rejected;
  assert.equal(requestSignal?.aborted, true);
  server.resolve(response(200, { token: "must-not-be-restored" }));
});

test("Google link start honors the current session cancellation signal", async () => {
  const sessions = new SessionOperations();
  const signal = sessions.capture();
  sessions.begin();
  let calls = 0;
  const api = new DeyeSolarApi(new ApiClient({ baseUrl, token: "original-account", transport: async () => { calls++; return response(200, {}); } }));
  await assert.rejects(api.startGoogleLink("challenge", "state", signal), { name: "AbortError" });
  assert.equal(calls, 0);
});

test("changing verification endpoint aborts the old request and only publishes the new challenge", async () => {
  const oldResponse = deferred<Awaited<ReturnType<ApiTransport>>>();
  let oldSignal: AbortSignal | undefined;
  const oldApi = new DeyeSolarApi(new ApiClient({ baseUrl, transport: async (_url, init) => { oldSignal = init.signal; return oldResponse.promise; } }));
  const calls: string[] = [];
  const nextApi = new DeyeSolarApi(new ApiClient({ baseUrl: "https://another.example", transport: async (url, init) => {
    calls.push(url);
    assert.equal(init.headers.Authorization, undefined);
    assert.deepEqual(JSON.parse(init.body!), { channel: "email", destination: "new@example.test", purpose: "register" });
    return response(200, challenge("new-endpoint"));
  } }));
  const requests = new VerificationRequests();
  const published: string[] = [];
  requests.replace(oldApi);
  const old = requests.start("email", "old@example.test", "register").then(result => { published.push(result.verificationId); });
  const rejected = assert.rejects(old, { name: "AbortError" });
  requests.replace(nextApi);
  await rejected;
  assert.equal(oldSignal?.aborted, true);
  published.push((await requests.start("email", "new@example.test", "register")).verificationId);
  oldResponse.resolve(response(200, challenge("old-endpoint")));
  await new Promise<void>(done => setImmediate(done));
  assert.deepEqual(published, ["new-endpoint"]);
  assert.deepEqual(calls, ["https://another.example/api/auth/verification/start"]);
});

test("verification replacement rejects a transport that ignores cancellation", async () => {
  const oldResponse = deferred<ReturnType<typeof challenge>>();
  const oldApi = { startVerification: async () => oldResponse.promise } as unknown as DeyeSolarApi;
  const requests = new VerificationRequests();
  requests.replace(oldApi);
  const old = requests.start("email", "old@example.test", "login");
  const rejected = assert.rejects(old, { name: "AbortError" });
  requests.cancel();
  requests.replace(null);
  oldResponse.resolve(challenge("must-not-return"));
  await rejected;
  await assert.rejects(requests.start("email", "new@example.test", "login"), /valid server URL/);
});

test("verification delivery errors permit retry on the same endpoint", async () => {
  let calls = 0;
  const api = new DeyeSolarApi(new ApiClient({ baseUrl, transport: async () => ++calls === 1
    ? response(503, { message: "Delivery unavailable" }) : response(200, challenge("retry")) }));
  const requests = new VerificationRequests();
  requests.replace(api);
  await assert.rejects(requests.start("email", "owner@example.test", "register"), /Delivery unavailable/);
  assert.equal((await requests.start("email", "owner@example.test", "register")).verificationId, "retry");
  requests.cancel();
  await assert.rejects(requests.start("email", "owner@example.test", "register"), { name: "AbortError" });
  assert.equal(calls, 2);
});
