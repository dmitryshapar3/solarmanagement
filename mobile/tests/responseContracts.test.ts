import assert from "node:assert/strict";
import { test } from "node:test";
import { ApiClient, ApiError } from "../src/core/api/ApiClient";
import { DeyeSolarApi } from "../src/core/api/DeyeSolarApi";
import { validApiResponse } from "../src/core/api/responseContracts";
import { integrationFixture } from "./support/integrationFixture";
const invalidMessage = "The server returned an invalid API response. Check the server URL and try again.";
for (const [path, payload] of [
  ["/api/auth/login", { token: 7, username: "owner", expiresAt: "2026-11-04T12:00:00Z" }],
  ["/api/auth/session", { authenticated: "true", username: "owner" }],
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
test("malformed token and expiry cannot reach successful sign-in persistence", async () => {
  const api = new DeyeSolarApi(new ApiClient({ baseUrl: "https://solar.example", transport: async () => ({ status: 200, ok: true,
    text: async () => JSON.stringify({ token: "otherwise-valid", username: "owner", expiresAt: "never" }) }) }));
  await assert.rejects(api.login("owner", "password"), { message: invalidMessage });
});
test("valid optional device state and future optional provider fields retain protocol compatibility", () => {
  const { provider } = integrationFixture();
  assert.equal(validApiResponse("/api/v2/integration-providers", "GET", { providers: [{ ...provider, futureOptionalField: "opaque" }], revision: "fixture" }), true);
  const device = { id: "socket", name: "Socket", category: null, online: true, isOn: false, currentPowerW: null };
  assert.equal(validApiResponse("/api/devices", "GET", { devices: [device], lastUpdated: null }), true);
  assert.equal(validApiResponse("/api/devices", "GET", { devices: [{ ...device, stateKnown: false }], lastUpdated: null }), true);
  assert.equal(validApiResponse("/api/devices", "GET", { devices: [{ ...device, currentPowerW: Number.POSITIVE_INFINITY }], lastUpdated: null }), false);
});
