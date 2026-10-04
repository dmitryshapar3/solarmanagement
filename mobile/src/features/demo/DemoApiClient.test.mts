import assert from "node:assert/strict";
import { test } from "node:test";
import { DeyeSolarApi } from "../../core/api/DeyeSolarApi";
import type { RuleRequest } from "../../core/api/types";
import { DemoApiClient } from "./DemoApiClient";

const clock = () => new Date("2026-09-30T11:25:00Z");
const httpStatus = (status: number) => (error: unknown) => typeof error === "object" && error !== null && "status" in error && error.status === status;

test("every existing screen API works offline, including device commands, rule CRUD and settings", async t => {
  const network = t.mock.method(globalThis, "fetch", () => { throw new Error("Demo must never use the network."); });
  const api = new DeyeSolarApi(new DemoApiClient(clock));
  assert.equal((await api.getSession()).authenticated, true);
  assert.ok((await api.getDashboard()).inverter);
  assert.ok((await api.refreshDashboard()).inverter);
  assert.ok((await api.getSolarEstimate()).estimate);
  for (const period of ["Today", "Week", "Month"] as const) assert.ok((await api.getSolarHistory(period)).points.length);
  for (const period of ["Day", "Month", "Year", "Custom"] as const) assert.ok((await api.getSales(period, "2026-09-30")).buckets.length);

  const devices = await api.getDevices(true);
  const selected = devices.devices[0]!;
  await api.integrations.sendDeviceCommand(selected.id, "00000000-0000-0000-0000-000000000001", false);
  assert.equal((await api.getDevices()).devices[0]!.isOn, false);
  assert.equal((await api.getDashboard()).manualDevices[0]!.isOn, false);
  assert.equal((await api.getRuleRuns(1, "OFF"))[0]!.action, "OFF");
  assert.ok((await api.getReadings(24)).length);

  const { id: _id, currentState: _state, currentStateChangedAt: _changed, lastEvaluated: _evaluated, ...original } = (await api.getRules())[0]!;
  const request: RuleRequest = { ...original, name: "Demo added rule" };
  const created = await api.createRule(request);
  assert.equal((await api.getRule(created.id)).name, request.name);
  const updated = await api.updateRule(created.id, { ...request, configurationVersion: created.configurationVersion, name: "Demo edited rule" });
  const disabled = await api.setRuleEnabled(created.id, false, updated.configurationVersion);
  assert.equal(disabled.enabled, false);
  assert.equal((await api.getRule(created.id)).name, "Demo edited rule");
  await api.deleteRule(created.id, disabled.configurationVersion);
  await assert.rejects(api.getRule(created.id), httpStatus(404));

  await api.savePolling({ intervalSeconds: 60 });
  await api.saveDisplay({ timeZoneId: "UTC" });
  const saved = await api.getSettings();
  assert.equal(saved.polling.intervalSeconds, 60);
  assert.equal(saved.display.timeZoneId, "UTC");
  await api.logout();
  assert.equal((await api.getSession()).authenticated, false);
  await assert.rejects(api.getDashboard(), httpStatus(401));
  assert.equal(network.mock.callCount(), 0);
});

test("demo cannot log in, bind a real server, forward a token or send an unknown route", async t => {
  const network = t.mock.method(globalThis, "fetch", () => { throw new Error("Demo must never use the network."); });
  const client = new DemoApiClient(clock);
  const api = new DeyeSolarApi(client);
  assert.throws(() => client.setBaseUrl("https://solar.dshapar.com"), /Leave demo/);
  client.setToken("synthetic-test-marker");
  await assert.rejects(api.login("", ""), httpStatus(400));
  for (const path of ["https://solar.dshapar.com/api/dashboard", "//solar.dshapar.com/api/dashboard", "/api/future-command"]) {
    await assert.rejects(client.request(path), httpStatus(404));
  }
  const canceled = new AbortController();
  canceled.abort();
  await assert.rejects(api.getDashboard(canceled.signal), { name: "AbortError" });
  assert.equal(network.mock.callCount(), 0);
});

test("demo state is isolated from returned snapshots and from another demo session", async () => {
  const first = new DeyeSolarApi(new DemoApiClient(clock));
  const second = new DeyeSolarApi(new DemoApiClient(clock));
  const snapshot = await first.getDevices();
  snapshot.devices[0]!.name = "Edited snapshot";
  assert.equal((await first.getDevices()).devices[0]!.name, "Demo water heater");
  await first.integrations.sendDeviceCommand(snapshot.devices[0]!.id, "00000000-0000-0000-0000-000000000001", false);
  assert.equal((await second.getDevices()).devices[0]!.isOn, true);
  const settings = await first.getSettings();
  settings.polling.intervalSeconds = 99;
  assert.equal((await first.getSettings()).polling.intervalSeconds, 30);
});

test("synthetic charts preserve local calendar boundaries, DST hours and provisional totals", async () => {
  const api = new DeyeSolarApi(new DemoApiClient(() => new Date("2026-10-25T23:00:00Z")));
  const generation = await api.getSolarHistory("Today", "2026-10-25");
  const sales = await api.getSales("Day", "2026-10-25");
  assert.equal(generation.selectedDate, "2026-10-25");
  assert.equal(generation.points.length, 25);
  assert.equal(sales.buckets.length, 25);
  assert.equal(sales.expectedHours, 25);
  assert.equal(sales.currentHour, null);
  const current = await new DeyeSolarApi(new DemoApiClient(clock)).getSales("Day", "2026-09-30");
  assert.equal(current.currentHour?.observedSeconds, 25 * 60);
  const completed = current.buckets.reduce((sum, item) => sum + (item.exportKwh ?? 0), 0);
  assert.ok(Math.abs(completed - current.exportKwh!) < .05);
  assert.equal(current.buckets.find(item => item.start === current.currentHour!.start)?.expectedHours, 0);
});


test("demo requires current configuration versions for edits, toggles and deletion", async () => {
  const client = new DemoApiClient(clock);
  const api = new DeyeSolarApi(client);
  const original = (await api.getRules())[0]!;
  await assert.rejects(client.request(`/api/rules/${original.id}/enabled`, { method: "PATCH", body: { enabled: false } }), httpStatus(428));
  const updated = await api.setRuleEnabled(original.id, false, original.configurationVersion);
  assert.notEqual(updated.configurationVersion, original.configurationVersion);
  await assert.rejects(api.setRuleEnabled(original.id, true, original.configurationVersion), httpStatus(409));
  await assert.rejects(api.updateRule(original.id, { ...original, name: "Stale edit" }), httpStatus(409));
  await assert.rejects(api.deleteRule(original.id, original.configurationVersion), httpStatus(409));
  await assert.rejects(client.request(`/api/rules/${original.id}`, { method: "DELETE" }), httpStatus(428));
  assert.equal((await api.getRule(original.id)).enabled, false);
  await api.deleteRule(original.id, updated.configurationVersion);
  await assert.rejects(api.getRule(original.id), httpStatus(404));
});


test("demo accepts only current receipt-based socket commands and keeps replay idempotent", async () => {
  const client = new DemoApiClient(clock);
  const api = new DeyeSolarApi(client);
  const device = (await api.getDevices()).devices[0]!;
  const commandId = "00000000-0000-0000-0000-000000000001";
  const receipt = await api.integrations.sendDeviceCommand(device.id, commandId, false);
  assert.equal(receipt.status, "acknowledged");
  assert.equal((await api.getDevices()).devices[0]!.isOn, false);
  assert.deepEqual(await api.integrations.getDeviceCommand(device.id, commandId), receipt);
  assert.deepEqual(await api.integrations.sendDeviceCommand(device.id, commandId, false), receipt);
  assert.equal((await api.getRuleRuns(1, "OFF")).filter(run => run.ruleName === "Demo manual override").length, 1);
  await assert.rejects(api.integrations.sendDeviceCommand(device.id, commandId, true), httpStatus(409));
  assert.deepEqual(await api.integrations.getUnresolvedCommands(device.id), []);
  for (const [path, method] of [["/api/devices/state", "POST"], ["/api/settings/deye", "PUT"], ["/api/settings/shelly", "PUT"], ["/api/settings/deye/stations", "GET"]] as const)
    await assert.rejects(client.request(path, { method, body: {} }), httpStatus(404));
});
