import assert from "node:assert/strict";
import { test } from "node:test";
import type { Device } from "../src/core/api/types";
import type { DeviceDetails, DeviceHistory } from "../src/core/api/redesignTypes";
import { currentObservedStateSince, readDeviceListHistories } from "../src/features/devices/deviceObservationPolicy";

const end = Date.parse("2026-10-06T12:00:00Z");
const at = (minutes: number) => new Date(end + minutes * 60_000).toISOString();
const device: Device = { id: "socket", name: "Socket", category: null, online: true, stateKnown: true, isOn: false, currentPowerW: 0 };
const history = (...intervals: DeviceHistory["intervals"]): DeviceHistory => ({ start: at(-1440), end: at(0), intervals, onSeconds: 0, knownSeconds: 600, partial: true });
const interval = (from: number, to: number, isOn: boolean | null = false, evidence = "provider_observation") => ({ from: at(from), to: at(to), isOn, evidence });

test("observed OFF start joins adjacent observations, stops at gaps and hides unavailable/current-state mismatch", () => {
  const value = history(interval(-30, -20), interval(-20, -10, null, "observation_gap"), interval(-10, -5), interval(-5, 0));
  assert.equal(currentObservedStateSince(device, value, end), at(-10));
  assert.equal(currentObservedStateSince({ ...device, isOn: true }, value, end), null);
  assert.equal(currentObservedStateSince({ ...device, online: false }, value, end), null);
  assert.equal(currentObservedStateSince({ ...device, stateKnown: false }, value, end), null);
});
test("acknowledgements, terminal gaps, stale/future history and malformed dates never manufacture a since timestamp", () => {
  for (const value of [history(interval(-10, 0, false, "provider_acknowledgement")), history(interval(-10, -5)),
    history(interval(-10, -5), interval(-5, 0, null, "observation_gap")), history({ ...interval(-10, 0), from: "invalid" })])
    assert.equal(currentObservedStateSince(device, value, end), null);
  assert.equal(currentObservedStateSince(device, history(interval(-10, 0)), end + 120_001), null);
  assert.equal(currentObservedStateSince(device, history(interval(-10, 0)), end - 10_001), null);
});
test("history requests are bounded to four and partial failures preserve other capable devices", async () => {
  const devices = Array.from({ length: 9 }, (_, id) => ({ ...device, id: String(id) }));
  const details = new Map(devices.map(device => [device.id, { supportsHistory: device.id !== "8" } as DeviceDetails]));
  let active = 0, maximum = 0; const visited: string[] = [];
  const results = await readDeviceListHistories(devices, details, async id => {
    visited.push(id); ++active; maximum = Math.max(maximum, active);
    await new Promise(resolve => setTimeout(resolve, 1)); --active;
    if (id === "0") throw new Error("Partial history failure");
    return history(interval(-5, 0));
  }, new AbortController().signal);
  assert.equal(maximum, 4); assert.equal(visited.length, 8); assert.equal(results.size, 7);
  assert.equal(results.has("0"), false); assert.equal(results.has("8"), false);
});
test("history collection aborts before returning obsolete account facts", async () => {
  const abort = new AbortController();
  const details = new Map([[device.id, { supportsHistory: true } as DeviceDetails]]);
  await assert.rejects(readDeviceListHistories([device], details, async () => {
    abort.abort(new Error("Account changed")); return history(interval(-5, 0));
  }, abort.signal), /Account changed/);
});
