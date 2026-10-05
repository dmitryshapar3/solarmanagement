import assert from "node:assert/strict";
import { test } from "node:test";
import { SocketCommandActions } from "../src/features/devices/SocketCommandActions";
import { SocketCommandCoordinator } from "../src/core/api/SocketCommandCoordinator";
import type { SocketCommandReceipt } from "../src/core/api/IntegrationApi";

function receipt(deviceId: string, commandId: string, isOn: boolean, status: SocketCommandReceipt["status"]): SocketCommandReceipt {
  return { deviceId, commandId, isOn, status, rejection: null, createdAt: "2026-10-05T11:00:00Z", completedAt: null };
}
function deferred() {
  let resolve!: (receipt: SocketCommandReceipt) => void;
  return { promise: new Promise<SocketCommandReceipt>(done => { resolve = done; }), resolve: (value: SocketCommandReceipt) => resolve(value) };
}

test("blur preserves the real receipt operation and fences its late acknowledgement without replay", async () => {
  const response = deferred(); let sends = 0, refreshes = 0;
  let dispatched!: () => void; const started = new Promise<void>(done => { dispatched = done; });
  const coordinator = new SocketCommandCoordinator({
    getUnresolvedCommands: async () => [],
    sendDeviceCommand: async () => { sends++; dispatched(); return response.promise; },
    getDeviceCommand: async () => receipt("device-a", "command-a", true, "acknowledged"),
    releaseDeviceCommand: async () => { assert.fail("Blur must never release a receipt"); }
  }, () => "command-a");
  const actions = new SocketCommandActions(coordinator, { changed: () => {}, started: () => {},
    acknowledged: async () => { refreshes++; }, failed: () => assert.fail("No stale errors") });
  actions.activate();
  const pending = actions.send("device-a", true);
  await started;
  actions.deactivate(); actions.activate();
  assert.equal(await actions.send("device-a", false), false);
  response.resolve(receipt("device-a", "command-a", true, "acknowledged"));
  await pending;
  assert.equal(sends, 1); assert.equal(refreshes, 0);
  assert.equal(coordinator.get("device-a")?.status, "acknowledged");
  assert.equal(actions.busy("device-a"), null);
});

test("a command from the revoked session cannot refresh the replacement account", async () => {
  const response = deferred(); let refreshes = 0, failures = 0;
  let dispatched!: () => void; const started = new Promise<void>(done => { dispatched = done; });
  const coordinator = new SocketCommandCoordinator({
    getUnresolvedCommands: async () => [], sendDeviceCommand: async () => { dispatched(); return response.promise; },
    getDeviceCommand: async () => receipt("device-a", "command-a", true, "pending"),
    releaseDeviceCommand: async () => receipt("device-a", "command-a", true, "uncertain_closed")
  }, () => "command-a");
  const actions = new SocketCommandActions(coordinator, { changed: () => {}, started: () => {},
    acknowledged: async () => { refreshes++; }, failed: () => { failures++; } });
  actions.activate(); const pending = actions.send("device-a", true);
  await started;
  coordinator.reset();
  response.resolve(receipt("device-a", "command-a", true, "acknowledged")); await pending;
  assert.equal(refreshes, 0); assert.equal(failures, 0); assert.equal(coordinator.get("device-a"), null);
});

test("independent devices retain separate pending gates", async () => {
  const first = deferred(), second = deferred(); const sent: string[] = [];
  let dispatched!: () => void; const started = new Promise<void>(done => { dispatched = done; });
  const coordinator = new SocketCommandCoordinator({
    getUnresolvedCommands: async () => [],
    sendDeviceCommand: async deviceId => { sent.push(deviceId); if (sent.length === 2) dispatched(); return deviceId === "device-a" ? first.promise : second.promise; },
    getDeviceCommand: async () => { throw new Error("Unused"); }, releaseDeviceCommand: async () => { throw new Error("Unused"); }
  }, (() => { let id = 0; return () => `command-${++id}`; })());
  const actions = new SocketCommandActions(coordinator, { changed: () => {}, started: () => {}, acknowledged: async () => {}, failed: () => {} });
  actions.activate(); const a = actions.send("device-a", true), b = actions.send("device-b", false);
  await started;
  assert.deepEqual(sent, ["device-a", "device-b"]);
  first.resolve(receipt("device-a", "command-1", true, "pending"));
  second.resolve(receipt("device-b", "command-2", false, "acknowledged")); await Promise.all([a, b]);
  assert.equal(coordinator.get("device-a")?.status, "pending");
  assert.equal(coordinator.get("device-b")?.status, "acknowledged");
});
