import assert from "node:assert/strict";
import { test } from "node:test";
import { DeyeSolarApi } from "../src/core/api/DeyeSolarApi";
import { DemoApiClient } from "../src/features/demo/DemoApiClient";

test("Renaming a demo device preserves rule references and restores its cloud name", async () => {
  const api = new DeyeSolarApi(new DemoApiClient());
  const before = await api.getDevices();
  const device = before.devices[0]!;
  const changed = await api.renameDevice(device.id, "Hot water");
  assert.equal(changed.id, device.id);
  assert.equal(changed.cloudName, device.name);
  assert.equal(changed.name, "Hot water");
  assert.ok((await api.getRules()).some(rule => rule.entityId === device.id));
  assert.equal((await api.getDashboard()).devices[0]!.name, "Hot water");
  assert.equal((await api.renameDevice(device.id, null)).name, device.name);
  const another = new DeyeSolarApi(new DemoApiClient());
  assert.equal((await another.getDevices()).devices[0]!.name, device.name);
});

test("Connection tests do not save settings or operate a demo socket", async () => {
  const api = new DeyeSolarApi(new DemoApiClient());
  const settings = await api.getSettings();
  const devices = await api.getDevices();
  await api.testIntegration("deye", { deyeCloud: { ...settings.deyeCloud, email: "fake@example.test" } });
  await api.testIntegration("shelly", { shelly: { ...settings.shelly, authKey: "fake" } });
  assert.deepEqual(await api.getSettings(), settings);
  assert.deepEqual((await api.getDevices()).devices, devices.devices);
});
