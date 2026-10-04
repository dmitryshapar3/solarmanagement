import assert from "node:assert/strict";
import { test } from "node:test";
import { ApiClient } from "../src/core/api/ApiClient";
import { DeyeSolarApi } from "../src/core/api/DeyeSolarApi";
import { IntegrationApi, type SocketCommandReceipt } from "../src/core/api/IntegrationApi";
import { SocketCommandCoordinator, commandUnresolved } from "../src/core/api/SocketCommandCoordinator";

function receipt(deviceId = "socket-a", commandId = "command-a", status = "pending", isOn = true): SocketCommandReceipt {
  return { deviceId, commandId, isOn, status, rejection: null, createdAt: "2026-10-04T10:00:00Z", completedAt: null };
}
function fixture() {
  const sends: { deviceId: string; commandId: string; isOn: boolean }[] = [];
  const reads: { deviceId: string; commandId: string }[] = [];
  const releases: string[] = [];
  const api = {
    getUnresolvedCommands: async (_device: string): Promise<SocketCommandReceipt[]> => [],
    sendDeviceCommand: async (deviceId: string, commandId: string, isOn: boolean) => {
      sends.push({ deviceId, commandId, isOn }); return receipt(deviceId, commandId, "acknowledged", isOn);
    },
    getDeviceCommand: async (deviceId: string, commandId: string) => {
      reads.push({ deviceId, commandId }); return receipt(deviceId, commandId, "acknowledged");
    },
    releaseDeviceCommand: async (deviceId: string, commandId: string) => {
      releases.push(commandId); return receipt(deviceId, commandId, "uncertain_closed");
    }
  };
  let next = 0;
  const commands = new SocketCommandCoordinator(api, () => `command-${++next}`);
  return { api, commands, sends, reads, releases };
}
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(done => { resolve = done; });
  return { promise, resolve };
}

test("a lost send response stays uncertain and result recovery reads the original operation without replay", async () => {
  const { api, commands, sends, reads } = fixture();
  api.sendDeviceCommand = async (deviceId, commandId, isOn) => { sends.push({ deviceId, commandId, isOn }); throw new Error("Lost response after the provider switched."); };
  assert.equal((await commands.send("socket-a", true)).status, "uncertain");
  await assert.rejects(commands.send("socket-a", false), /previous command/);
  assert.equal(sends.length, 1);
  assert.equal((await commands.check("socket-a")).status, "acknowledged");
  assert.deepEqual(reads, [{ deviceId: "socket-a", commandId: "command-1" }]);
  assert.equal(sends.length, 1);
  assert.equal(commandUnresolved(commands.get("socket-a")), false);
});

test("durable recovery after restart blocks a different command and leaves neighboring devices untouched", async () => {
  const { api, commands, sends } = fixture();
  api.getUnresolvedCommands = async id => id === "socket-a" ? [receipt("socket-a", "original-command", "requested", false)] : [];
  await assert.rejects(commands.send("socket-a", true), /previous command/);
  assert.equal(commands.get("socket-a")!.commandId, "original-command");
  assert.equal(commands.get("socket-a")!.status, "pending");
  assert.equal(sends.length, 0);
  assert.equal((await commands.send("socket-b", false)).status, "acknowledged");
  assert.deepEqual(sends, [{ deviceId: "socket-b", commandId: "command-1", isOn: false }]);
  assert.equal(commands.get("socket-a")!.isOn, false);
  const restarted = new SocketCommandCoordinator(api, () => "new-id-must-not-be-used");
  await restarted.recover("socket-a");
  assert.equal(restarted.get("socket-a")!.commandId, "original-command");
  await assert.rejects(restarted.send("socket-a", true), /previous command/);
  assert.equal(sends.length, 1);
});

test("unavailable or foreign recovery receipts prevent a POST instead of treating unknown data as an empty journal", async () => {
  const { api, commands, sends } = fixture();
  api.getUnresolvedCommands = async () => { throw new Error("Journal unavailable"); };
  await assert.rejects(commands.send("socket-a", true), /Journal unavailable/);
  assert.equal(commands.isRunning("socket-a"), false);
  api.getUnresolvedCommands = async () => [receipt("another-tenant-socket")];
  await assert.rejects(commands.send("socket-a", true), /did not identify/);
  assert.equal(commands.get("socket-a"), null);
  assert.equal(sends.length, 0);
});

test("mismatched command, device or desired state can never become an acknowledged command", async () => {
  for (const wrong of [receipt("socket-a", "wrong-command", "acknowledged"), receipt("socket-b", "command-1", "acknowledged"), receipt("socket-a", "command-1", "acknowledged", false)]) {
    const { api, commands } = fixture();
    api.sendDeviceCommand = async () => wrong;
    assert.equal((await commands.send("socket-a", true)).status, "uncertain");
    api.getDeviceCommand = async () => wrong;
    assert.equal((await commands.check("socket-a")).status, "uncertain");
    assert.equal(commands.get("socket-a")!.commandId, "command-1");
    assert.equal(commands.get("socket-a")!.isOn, true);
  }
});

test("the per-device gate covers recovery and in-flight send while another device may execute", async () => {
  const { api, commands, sends } = fixture();
  const held = deferred<SocketCommandReceipt[]>();
  api.getUnresolvedCommands = id => id === "socket-a" ? held.promise : Promise.resolve([]);
  const first = commands.send("socket-a", true);
  await assert.rejects(commands.send("socket-a", false), /previous command/);
  await commands.send("socket-b", false);
  held.resolve([]);
  await first;
  assert.equal(sends.length, 2);
  assert.equal(sends.filter(send => send.deviceId === "socket-a").length, 1);
  assert.equal(commands.isRunning("socket-a"), false);
});

test("an older recovery snapshot cannot clear or replace an operation started while it was loading", async () => {
  const { api, commands } = fixture();
  const stale = deferred<SocketCommandReceipt[]>();
  let recovering = true;
  api.getUnresolvedCommands = () => recovering ? stale.promise : Promise.resolve([]);
  const recovery = commands.recover("socket-a");
  recovering = false;
  api.sendDeviceCommand = async (deviceId, commandId, isOn) => receipt(deviceId, commandId, "uncertain", isOn);
  await commands.send("socket-a", true);
  stale.resolve([receipt("socket-a", "obsolete-command")]);
  await recovery;
  assert.equal(commands.get("socket-a")!.commandId, "command-1");
  api.getUnresolvedCommands = async () => [];
  await commands.recover("socket-a");
  assert.equal(commands.get("socket-a")!.status, "uncertain");
});

test("reset fences a late receipt and cannot clear the replacement session's running operation", async () => {
  const { api, commands } = fixture();
  const firstResponse = deferred<SocketCommandReceipt>();
  const secondResponse = deferred<SocketCommandReceipt>();
  api.sendDeviceCommand = (_id, commandId) => commandId === "command-1" ? firstResponse.promise : secondResponse.promise;
  const first = commands.send("socket-a", true);
  await Promise.resolve(); await Promise.resolve();
  commands.reset();
  const second = commands.send("socket-a", false);
  await Promise.resolve(); await Promise.resolve();
  firstResponse.resolve(receipt("socket-a", "command-1", "acknowledged"));
  await assert.rejects(first, /session changed/);
  assert.equal(commands.isRunning("socket-a"), true);
  assert.equal(commands.get("socket-a")!.commandId, "command-2");
  secondResponse.resolve(receipt("socket-a", "command-2", "acknowledged", false));
  await second;
  assert.equal(commands.get("socket-a")!.isOn, false);
});

test("explicit release permits a new command while preserving an unknown previous result", async () => {
  const { api, commands, sends, releases } = fixture();
  api.sendDeviceCommand = async (deviceId, commandId, isOn) => { sends.push({ deviceId, commandId, isOn }); return receipt(deviceId, commandId, "uncertain", isOn); };
  await commands.send("socket-a", true);
  assert.equal((await commands.check("socket-a", true)).status, "uncertain_closed");
  assert.deepEqual(releases, ["command-1"]);
  assert.equal(commandUnresolved(commands.get("socket-a")), false);
  assert.equal(sends.length, 1);
  await commands.send("socket-a", false);
  assert.deepEqual(sends.map(send => send.commandId), ["command-1", "command-2"]);
});

test("a recovered pending command can be checked but cannot be abandoned by a stale release callback", async () => {
  const { api, commands, releases, reads, sends } = fixture();
  api.getUnresolvedCommands = async () => [receipt("socket-a", "still-executing", "pending")];
  await commands.recover("socket-a");
  await assert.rejects(commands.check("socket-a", true), /still pending/);
  assert.deepEqual(releases, []);
  assert.deepEqual(sends, []);
  assert.equal(commands.get("socket-a")!.status, "pending");
  assert.equal((await commands.check("socket-a")).status, "acknowledged");
  assert.deepEqual(reads, [{ deviceId: "socket-a", commandId: "still-executing" }]);
});

test("a transient result-read failure preserves known pending status and cannot expose release", async () => {
  const { api, commands, releases, sends } = fixture();
  api.getUnresolvedCommands = async () => [receipt("socket-a", "still-executing", "pending")];
  await commands.recover("socket-a");
  api.getDeviceCommand = async () => { throw new Error("Temporary network failure"); };
  assert.equal((await commands.check("socket-a")).status, "pending");
  assert.equal(commands.get("socket-a")!.commandId, "still-executing");
  await assert.rejects(commands.check("socket-a", true), /still pending/);
  assert.deepEqual(releases, []);
  assert.deepEqual(sends, []);
});

test("the authenticated client sends only one UUID-bearing command and uses read/release routes without replay", async () => {
  const calls: { method: string; path: string; body: unknown; token?: string }[] = [];
  const client = new ApiClient({ baseUrl: "https://solar.example", token: "session-a", transport: async (url, init) => {
    calls.push({ method: init.method, path: new URL(url).pathname + new URL(url).search, body: init.body ? JSON.parse(init.body) : null, token: init.headers.Authorization });
    return { status: 200, ok: true, text: async () => JSON.stringify(new URL(url).searchParams.has("unresolved") ? [receipt()] : receipt()) };
  } });
  const api = new IntegrationApi(client);
  await api.sendDeviceCommand("socket-a", "command-a", true);
  await api.getDeviceCommand("socket-a", "command-a");
  await api.getUnresolvedCommands("socket-a");
  await api.releaseDeviceCommand("socket-a", "command-a");
  assert.deepEqual(calls.map(call => [call.method, call.path, call.body]), [
    ["POST", "/api/v2/devices/socket-a/commands", { commandId: "command-a", isOn: true }],
    ["GET", "/api/v2/devices/socket-a/commands/command-a", null],
    ["GET", "/api/v2/devices/socket-a/commands?unresolved=true", null],
    ["POST", "/api/v2/devices/socket-a/commands/command-a/release", null]
  ]);
  assert.equal(calls.every(call => call.token === "Bearer session-a"), true);
  const host = new DeyeSolarApi(client);
  await host.socketCommands.recover("socket-a");
  assert.equal(host.socketCommands.get("socket-a")!.commandId, "command-a");
  client.setToken("session-b");
  assert.equal(host.socketCommands.get("socket-a"), null);
});
