import assert from "node:assert/strict";
import test from "node:test";
import { ManualOverrideCommand } from "../src/features/dashboard/ManualOverrideCommand";

function deferred() {
  let resolve!: () => void;
  let reject!: (error: Error) => void;
  const promise = new Promise<void>((success, failure) => { resolve = success; reject = failure; });
  return { promise, resolve, reject };
}

function callbacks() {
  const busy: Array<string | null> = [];
  const errors: unknown[] = [];
  let completed = 0;
  return {
    busy, errors, get completed() { return completed; },
    handlers: { busyChanged: (value: "on" | "off" | null) => { busy.push(value); }, completed: () => { ++completed; }, failed: (error: unknown) => { errors.push(error); } }
  };
}

test("manual override cannot overlap after blur and refocus while the first device command is pending", async () => {
  const gate = new ManualOverrideCommand();
  const request = deferred();
  const result = callbacks();
  const sent: string[] = [];
  gate.activate();
  const first = gate.run("on", () => { sent.push("on"); return request.promise; }, result.handlers);
  gate.deactivate();
  gate.activate();
  const overlap = await gate.run("off", async () => { sent.push("off"); }, result.handlers);
  assert.equal(overlap, false);
  assert.deepEqual(sent, ["on"]);
  assert.equal(gate.busy, "on");
  request.resolve();
  assert.equal(await first, true);
  assert.equal(result.completed, 0);
  assert.equal(gate.busy, null);
  assert.equal(await gate.run("off", async () => { sent.push("off"); }, result.handlers), true);
  assert.deepEqual(sent, ["on", "off"]);
});

test("a rejected command after refocus releases its gate without publishing an old error", async () => {
  const gate = new ManualOverrideCommand();
  const request = deferred();
  const result = callbacks();
  gate.activate();
  const first = gate.run("on", () => request.promise, result.handlers);
  gate.deactivate();
  gate.activate();
  request.reject(new Error("Old request failed"));
  await first;
  assert.equal(gate.busy, null);
  assert.deepEqual(result.errors, []);
  assert.deepEqual(result.busy, ["on", null]);
  assert.equal(await gate.run("off", async () => {}, result.handlers), true);
});

test("an active command failure clears busy and permits a successful retry", async () => {
  const gate = new ManualOverrideCommand();
  const result = callbacks();
  const error = new Error("Device unavailable");
  gate.activate();
  await gate.run("on", async () => { throw error; }, result.handlers);
  assert.deepEqual(result.errors, [error]);
  assert.equal(gate.busy, null);
  await gate.run("on", async () => {}, result.handlers);
  assert.equal(result.completed, 1);
  assert.deepEqual(result.busy, ["on", null, "on", null]);
});

test("the gate also protects the acknowledged command while its dashboard refresh finishes", async () => {
  const gate = new ManualOverrideCommand();
  const refresh = deferred();
  const result = callbacks();
  gate.activate();
  const first = gate.run("on", async () => {}, { ...result.handlers, completed: () => refresh.promise });
  await Promise.resolve();
  assert.equal(await gate.run("off", async () => { assert.fail("No second device command may be sent"); }, result.handlers), false);
  refresh.resolve();
  await first;
  assert.equal(gate.busy, null);
});

test("leaving the screen fences all completion UI callbacks without abandoning the command", async () => {
  const gate = new ManualOverrideCommand();
  const request = deferred();
  const result = callbacks();
  gate.activate();
  const first = gate.run("on", () => request.promise, result.handlers);
  gate.deactivate();
  request.resolve();
  await first;
  assert.equal(result.completed, 0);
  assert.deepEqual(result.busy, ["on"]);
  assert.equal(gate.busy, null);
  assert.equal(await gate.run("off", async () => { assert.fail("Inactive screen"); }, result.handlers), false);
});
