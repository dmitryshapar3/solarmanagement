import assert from "node:assert/strict";
import { test } from "node:test";
import { ApiClient, ApiError, normalizeBaseUrl } from "../src/core/api/ApiClient";
import { DEFAULT_API_BASE_URL } from "../src/core/api/config";

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(done => { resolve = done; });
  return { promise, resolve };
}

test("fresh installations use the production HTTPS endpoint; explicit LAN development URLs remain valid", () => {
  assert.equal(DEFAULT_API_BASE_URL, "https://solar.dshapar.com");
  assert.equal(normalizeBaseUrl(" HTTPS://SOLAR.DSHAPAR.COM:443/ "), DEFAULT_API_BASE_URL);
  assert.equal(normalizeBaseUrl("http://192.168.31.190:5000/solar/"), "http://192.168.31.190:5000/solar");
  for (const value of ["", "solar.dshapar.com", "ftp://host", "https://user:secret@host", "https://host/?token=x", "https://host/#x"]) {
    assert.throws(() => normalizeBaseUrl(value));
  }
});

test("requests preserve JSON, query values and their captured bearer with the required transport policy", async t => {
  t.mock.method(globalThis, "fetch", async (url: string, init: RequestInit) => {
    assert.equal(url, "https://solar.example/api/read?name=a+b&zero=0&enabled=false");
    assert.equal((init.headers as Record<string, string>).Authorization, "Bearer bound-token");
    assert.equal(init.body, '{"watts":125}');
    assert.equal(init.redirect, "error");
    assert.equal(init.credentials, "omit");
    assert.ok(init.signal);
    return new Response('{"ok":true}');
  });
  const client = new ApiClient({ baseUrl: "https://solar.example/", token: "bound-token" });
  assert.deepEqual(await client.request("/api/read", { method: "POST", body: { watts: 125 },
    query: { name: "a b", zero: 0, enabled: false, absent: undefined, missing: null } }), { ok: true });
});

test("the native transport receives redirect and credential policy instead of using the global XHR fallback", async t => {
  t.mock.method(globalThis, "fetch", () => { throw new Error("Global fetch must not run."); });
  let calls = 0;
  const client = new ApiClient({ baseUrl: "https://solar.example", token: "bound-token", transport: async (url, init) => {
    calls++;
    assert.equal(url, "https://solar.example/api/read");
    assert.equal(init.redirect, "error");
    assert.equal(init.credentials, "omit");
    assert.equal(init.headers.Authorization, "Bearer bound-token");
    return new Response('{"ok":true}');
  } });
  assert.deepEqual(await client.request("/api/read"), { ok: true });
  assert.equal(calls, 1);
});

for (const replacement of ["https://foreign.example", "http://solar.example", "https://solar.example/other"]) {
  test(`changing the server to ${replacement} removes the old token before any new request`, async t => {
    const requests: { url: string; authorization: string | undefined }[] = [];
    t.mock.method(globalThis, "fetch", async (url: string, init: RequestInit) => {
      requests.push({ url, authorization: (init.headers as Record<string, string>).Authorization });
      return new Response("{}");
    });
    const client = new ApiClient({ baseUrl: "https://solar.example", token: "old-token" });
    await client.request("/first");
    client.setBaseUrl(replacement);
    await client.request("/next");
    assert.deepEqual(requests, [
      { url: "https://solar.example/first", authorization: "Bearer old-token" },
      { url: `${replacement}/next`, authorization: undefined }
    ]);
  });
}

for (const status of [200, 401]) {
  test(`late ${status} from a replaced session is rejected without affecting its successor`, async t => {
    const late = deferred<Response>();
    let unauthorized = 0;
    t.mock.method(globalThis, "fetch", () => late.promise);
    const client = new ApiClient({ baseUrl: "https://solar.example", token: "old", onUnauthorized: () => unauthorized++ });
    const pending = client.request("/session");
    const rejected = assert.rejects(pending, { name: "AbortError" });
    client.setToken("new");
    await rejected;
    late.resolve(new Response('{"old":true}', { status }));
    await new Promise(resolve => setTimeout(resolve, 0));
    assert.equal(unauthorized, 0);
  });
}

test("a current 401 expires its own session, but a failed login does not", async t => {
  t.mock.method(globalThis, "fetch", async () => new Response('{"message":"Denied"}', { status: 401 }));
  let unauthorized = 0;
  const client = new ApiClient({ baseUrl: "https://solar.example", token: "current", onUnauthorized: () => unauthorized++ });
  await assert.rejects(client.request("/login", { skipUnauthorizedHandler: true }), error => error instanceof ApiError && error.status === 401);
  assert.equal(unauthorized, 0);
  await assert.rejects(client.request("/session"), error => error instanceof ApiError && error.status === 401);
  assert.equal(unauthorized, 1);
});

test("caller cancellation and already-canceled requests release promptly without fetching again", async t => {
  let calls = 0;
  let requestSignal: AbortSignal | null | undefined;
  t.mock.method(globalThis, "fetch", (_url: string, init: RequestInit) => {
    calls++;
    requestSignal = init.signal;
    return new Promise<Response>(() => {});
  });
  const client = new ApiClient({ baseUrl: "https://solar.example" });
  const cancel = new AbortController();
  const pending = client.request("/read", { signal: cancel.signal });
  const rejected = assert.rejects(pending, { name: "AbortError" });
  cancel.abort();
  await rejected;
  assert.equal(requestSignal?.aborted, true);
  await assert.rejects(client.request("/read", { signal: cancel.signal }), { name: "AbortError" });
  assert.equal(calls, 1);
});

for (const stalled of ["fetch", "body"]) {
  test(`a stalled ${stalled} is bounded even when the transport ignores abort`, { timeout: 1000 }, async t => {
    t.mock.method(globalThis, "fetch", () => stalled === "fetch" ? new Promise<Response>(() => {})
      : Promise.resolve({ text: () => new Promise<string>(() => {}) } as Response));
    const client = new ApiClient({ baseUrl: "https://solar.example" });
    await assert.rejects(client.request("/read", { timeoutMs: 10 }), { name: "TimeoutError" });
  });
}

test("invalid paths and timeout bounds have no network effects; proxy HTML is not shown as an error", async t => {
  let calls = 0;
  t.mock.method(globalThis, "fetch", async () => { calls++; return new Response("<html>private upstream detail</html>", { status: 502 }); });
  const client = new ApiClient({ baseUrl: "https://solar.example" });
  for (const path of ["https://foreign.example", "//foreign.example"]) await assert.rejects(client.request(path));
  await assert.rejects(client.request("/read", { timeoutMs: 0 }));
  assert.equal(calls, 0);
  await assert.rejects(client.request("/read"), { message: "Request failed with HTTP 502." });
});

for (const [description, body] of [
  ["proxy HTML", "<html>private upstream detail and echoed-secret</html>"],
  ["malformed JSON", '{"private":"echoed-secret",']
] as const) {
  test(`successful ${description} is rejected safely instead of being returned as API data`, async t => {
    t.mock.method(globalThis, "fetch", async () => new Response(body, { status: 200 }));
    let unauthorized = 0;
    const client = new ApiClient({ baseUrl: "https://solar.example", token: "current", onUnauthorized: () => unauthorized++ });
    await assert.rejects(client.request("/read"), error => error instanceof ApiError && error.status === 200 &&
      error.message === "The server returned an invalid API response. Check the server URL and try again.");
    assert.equal(unauthorized, 0);
  });
}

test("an empty 204 response completes a write without requiring a JSON body", async t => {
  t.mock.method(globalThis, "fetch", async () => new Response(null, { status: 204 }));
  const client = new ApiClient({ baseUrl: "https://solar.example" });
  assert.equal(await client.request<void>("/settings", { method: "PUT", body: { interval: 5 } }), undefined);
});

test("a current server access denial invalidates connected data without signing out; late denial cannot affect a replacement", async () => {
  let denied = 0;
  const old = deferred<Response>();
  let first = true;
  const client = new ApiClient({ baseUrl: "https://solar.example", token: "old", transport: async () => {
    if (first) { first = false; return old.promise; }
    return new Response('{"message":"Subscription required"}', { status: 402 });
  } });
  const unsubscribe = client.onBillingDenied(() => denied++);
  const oldRequest = client.request("/api/devices");
  const canceled = assert.rejects(oldRequest, { name: "AbortError" });
  client.setToken("new");
  await canceled;
  old.resolve(new Response('{}', { status: 402 }));
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.equal(denied, 0);
  await assert.rejects(client.request("/api/devices"), (error: unknown) => error instanceof ApiError && error.status === 402);
  assert.equal(denied, 1);
  unsubscribe();
  await assert.rejects(client.request("/api/devices"));
  assert.equal(denied, 1);
});

test("a trial socket quota failure preserves valid account access and its integration form", async () => {
  let denied = 0;
  const client = new ApiClient({ baseUrl: "https://solar.example", token: "owner", transport: async (_url, init) => {
    assert.equal(init.headers.Authorization, "Bearer owner");
    return new Response('{"code":"trial_socket_limit","message":"The trial allows one socket. Subscribe to add more sockets."}', { status: 402 });
  } });
  client.onBillingDenied(() => denied++);
  await assert.rejects(client.request("/api/integrations/device/selection", { method: "POST" }),
    (error: unknown) => error instanceof ApiError && error.status === 402 && error.message.includes("one socket"));
  assert.equal(denied, 0);
});
