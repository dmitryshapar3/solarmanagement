import assert from "node:assert/strict";
import { test } from "node:test";
import { ApiClient, ApiError } from "../src/core/api/ApiClient";
import { DeyeSolarApi } from "../src/core/api/DeyeSolarApi";
test("fresh proof failure keeps the authenticated account; successful revoke carries proof only to its bound HTTPS endpoint", async () => {
  let expired = 0; const calls: { path: string; body?: string; bearer?: string }[] = [];
  const api = new DeyeSolarApi(new ApiClient({ baseUrl: "https://solar.example", token: "bound-session", onUnauthorized: () => expired++, transport: async (url, init) => {
    const path = new URL(url).pathname; calls.push({ path, body: init.body, bearer: init.headers.Authorization });
    const bad = init.body?.includes("wrong");
    return { status: bad ? 401 : 200, ok: !bad, text: async () => JSON.stringify(bad ? { code: "fresh_proof_required", message: "Confirm your current password or a new verification code before continuing." } : { signedOut: true }) };
  } }));
  await assert.rejects(api.accountSecurity.revokeAll({ currentPassword: "wrong" }), error => error instanceof ApiError && error.status === 401);
  assert.equal(expired, 0);
  assert.deepEqual(await api.accountSecurity.revokeAll({ currentPassword: "valid" }), { signedOut: true });
  assert.deepEqual(calls, [
    { path: "/api/auth/security/revoke-all", body: '{"currentPassword":"wrong"}', bearer: "Bearer bound-session" },
    { path: "/api/auth/security/revoke-all", body: '{"currentPassword":"valid"}', bearer: "Bearer bound-session" }
  ]);
});
test("account deletion conflict remains recoverable; malformed success cannot cause local sign-out", async () => {
  let count = 0;
  const api = new DeyeSolarApi(new ApiClient({ baseUrl: "https://solar.example", token: "owner", transport: async () => ++count === 1
    ? { status: 409, ok: false, text: async () => JSON.stringify({ code: "unresolved_commands", message: "Resolve outstanding device commands before deleting this account." }) }
    : { status: 200, ok: true, text: async () => JSON.stringify({ deleted: "true" }) } }));
  await assert.rejects(api.accountSecurity.deleteAccount({ currentPassword: "proof" }), error => error instanceof ApiError && error.status === 409);
  await assert.rejects(api.accountSecurity.deleteAccount({ currentPassword: "proof" }), /invalid API response/);
});
