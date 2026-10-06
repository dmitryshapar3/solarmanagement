import assert from "node:assert/strict";
import { test } from "node:test";
import { DeyeSolarApi } from "../../core/api/DeyeSolarApi";
import type { RuleRequest } from "../../core/api/types";
import { DemoApiClient } from "./DemoApiClient";

const clock = () => new Date("2026-09-30T11:25:00Z");
const httpStatus = (status: number) => (error: unknown) => typeof error === "object" && error !== null && "status" in error && error.status === status;

test("every screen API works offline; commands stay disabled while rule CRUD and settings remain local", async t => {
  const network = t.mock.method(globalThis, "fetch", () => { throw new Error("Demo must never use the network."); });
  const api = new DeyeSolarApi(new DemoApiClient(clock));
  assert.equal((await api.getSession()).authenticated, true);
  assert.ok((await api.getDashboard()).inverter);
  assert.ok((await api.refreshDashboard()).inverter);
  assert.ok((await api.getSolarEstimate()).estimate);
  for (const period of ["Today", "Week", "Month"] as const) assert.ok((await api.getSolarHistory(period)).points.length);
  for (const period of ["Day", "Month", "Year", "Custom"] as const) assert.ok((await api.getSales(period, "2026-09-30", undefined, period === "Custom" ? { from: "2026-09-01", through: "2026-09-30" } : undefined)).buckets.length);

  const devices = await api.getDevices(true);
  const selected = devices.devices[0]!;
  await assert.rejects(api.integrations.sendDeviceCommand(selected.id, "00000000-0000-0000-0000-000000000001", false), httpStatus(403));
  assert.equal((await api.getDevices()).devices[0]!.isOn, selected.isOn);
  assert.equal((await api.getDashboard()).manualDevices[0]!.isOn, selected.isOn);
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
  await first.renameDevice(snapshot.devices[0]!.id, "First session name");
  assert.equal((await second.getDevices()).devices[0]!.name, "Demo water heater");
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


test("offline demo rejects all hardware commands, creates no receipts and preserves observed state", async t => {
  const network = t.mock.method(globalThis, "fetch", () => { throw new Error("Demo must never use the network."); });
  const api = new DeyeSolarApi(new DemoApiClient(clock)); const before = await api.getDevices(); const runs = await api.getRuleRuns(24);
  for (const device of before.devices) {
    for (const choice of [undefined, "pause", "once"] as const) await assert.rejects(api.integrations.sendDeviceCommand(device.id, "00000000-0000-0000-0000-000000000001", !device.isOn, choice), httpStatus(403));
    await assert.rejects(api.integrations.getDeviceCommand(device.id, "00000000-0000-0000-0000-000000000001"), httpStatus(404));
    assert.deepEqual(await api.integrations.getUnresolvedCommands(device.id), []);
    assert.equal((await api.getDeviceDetails(device.id)).canSwitch, false);
  }
  assert.deepEqual(await api.getDevices(), before); assert.deepEqual(await api.getRuleRuns(24), runs); assert.equal(network.mock.callCount(), 0);
});

test("GUID sample sockets keep their reported power through local renaming without switching", async () => {
  const api = new DeyeSolarApi(new DemoApiClient(clock)); const [heater, lights] = (await api.getDevices()).devices; assert.ok(heater && lights);
  assert.match(lights.id, /^[a-f0-9-]{36}$/); await api.renameDevice(lights.id, "Renamed fixture");
  const after = (await api.getDevices()).devices;
  assert.equal(after.find(device => device.id === lights.id)!.name, "Renamed fixture");
  assert.equal(after.find(device => device.id === lights.id)!.currentPowerW, lights.currentPowerW);
  assert.equal(after.find(device => device.id === heater.id)!.currentPowerW, heater.currentPowerW);
});

test("retargeting a demo rule reconciles its new observed device and clears the prior evaluation", async () => {
  let now = new Date("2026-10-05T11:00:00Z");
  const api = new DeyeSolarApi(new DemoApiClient(() => now));
  const original = (await api.getRules())[0]!;
  assert.equal(original.currentState, true);
  const offTarget = (await api.getDevices()).devices.find(device => !device.isOn)!;
  now = new Date("2026-10-05T12:00:00Z");
  const moved = await api.updateRule(original.id, { ...original, entityId: offTarget.id, currentState: true } as typeof original);
  assert.equal(moved.currentState, false);
  assert.equal(moved.currentStateChangedAt, now.toISOString());
  assert.equal(moved.lastEvaluated, null);
  assert.notEqual(moved.configurationVersion, original.configurationVersion);
  const renamed = await api.updateRule(moved.id, { ...moved, name: "Same target", currentState: true } as typeof moved);
  assert.equal(renamed.currentState, false, "A same-target config edit cannot accept caller-supplied runtime state");
  assert.equal(renamed.currentStateChangedAt, moved.currentStateChangedAt);
  assert.equal(renamed.lastEvaluated, null);
  assert.equal((await api.getDevices()).devices.find(device => device.id === offTarget.id)!.isOn, false);
});

test("demo aggregate settings are atomic and version fenced", async () => {
  const api = new DeyeSolarApi(new DemoApiClient(clock)); const before = await api.request<any>("/api/settings/installation");
  await assert.rejects(api.request("/api/settings/installation", { method: "PUT", body: { ...before, expectedVersion: before.version, expectedIntegrationVersions: before.integrationVersions, site: { ...before.site, solarEstimate: { ...before.site.solarEstimate, locationLabel: "Must roll back" } }, display: { timeZoneId: "Invalid/zone" } } }), httpStatus(400));
  assert.deepEqual(await api.request("/api/settings/installation"), before);
  const after = await api.request<any>("/api/settings/installation", { method: "PUT", body: { ...before, expectedVersion: before.version, expectedIntegrationVersions: before.integrationVersions, polling: { intervalSeconds: 60 } } });
  assert.notEqual(after.version, before.version); assert.equal(after.polling.intervalSeconds, 60);
  await assert.rejects(api.request("/api/settings/installation", { method: "PUT", body: { ...before, expectedVersion: before.version } }), httpStatus(409));
});
test("demo redesign data provides current-hour separation, inclusive custom dates and 5-minute paging offline", async t => {
  const network = t.mock.method(globalThis, "fetch", () => { throw new Error("Demo must never use the network."); }); const api = new DeyeSolarApi(new DemoApiClient(clock));
  const data = await api.getSalesDetails("Custom", "2026-09-30", undefined, { from: "2026-09-01", through: "2026-09-30" });
  assert.equal(data.request.from, "2026-09-01"); assert.equal(data.request.through, "2026-09-30"); assert.equal(data.buckets.length, 30); assert.ok(data.hours!.length); assert.ok(!data.hours!.some(hour => hour.start === data.currentHour?.start));
  const production = await api.getProduction("Today"); assert.ok(production.hours.length); assert.equal(production.currentHour?.actualKw, null);
  const first = await api.getReadingsView(168, "5m"); assert.equal(Date.parse(first.items[0]!.timestamp) - Date.parse(first.items[1]!.timestamp), 300000); assert.ok(first.nextCursor);
  const next = await api.getReadingsView(168, "5m", first.nextCursor!); assert.ok(next.items.length); assert.ok(first.items.every(row => next.items.every(other => other.id !== row.id)));
  const activity = await api.getActivity(); assert.ok(activity.items.length); assert.ok((await api.getActivityChecks(activity.items[0]!.id)).items.length); assert.equal(network.mock.callCount(), 0);
});
