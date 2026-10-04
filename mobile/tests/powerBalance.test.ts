import assert from "node:assert/strict";
import test from "node:test";
import { balanceDirection, batteryFlow, calculatePowerBalance, formatBalanceWatts, reportedPowerBalance } from "../src/features/dashboard/powerBalance";

const timestamp = "2026-10-01T10:00:00Z";
const now = Date.parse(timestamp) + 60_000;
const reading = (changes = {}) => ({ solarProduction: 4100, gridConsumption: -1200, batteryPower: -2000, loadPower: 900, timestamp, ...changes });

test("battery charging and discharging display their magnitude without changing solar production", () => {
  assert.deepEqual(batteryFlow(-2742), { label: "Battery charging", watts: 2742 });
  assert.deepEqual(batteryFlow(2742), { label: "Battery discharging", watts: 2742 });
  assert.deepEqual(batteryFlow(0), { label: "Battery idle", watts: 0 });
  assert.deepEqual(batteryFlow(-2147483648), { label: "Battery charging", watts: 2147483648 });
  for (const value of [undefined, null, Number.NaN, Infinity])
    assert.deepEqual(batteryFlow(value), { label: "Battery power", watts: null });
});

test("balanced charging, discharging, importing and exporting retain the power signs", () => {
  assert.equal(calculatePowerBalance(reading()), 0); // PV supplies load, charging and export.
  assert.equal(calculatePowerBalance(reading({ solarProduction: 0, gridConsumption: 1000, batteryPower: 500, loadPower: 1500 })), 0);
  assert.equal(calculatePowerBalance(reading({ solarProduction: 0, gridConsumption: 3000, batteryPower: -2000, loadPower: 1000 })), 0);
  assert.equal(calculatePowerBalance(reading({ solarProduction: 5000, gridConsumption: -6000, batteryPower: 1500, loadPower: 500 })), 0);
});

test("positive and negative balance differences are distinct and never converted to absolute losses", () => {
  const positive = calculatePowerBalance(reading({ batteryPower: -1800 }));
  const negative = calculatePowerBalance(reading({ batteryPower: -2200 }));
  assert.equal(positive, 200);
  assert.equal(negative, -200);
  assert.equal(formatBalanceWatts(positive), "+200 W");
  assert.equal(formatBalanceWatts(negative), "-200 W");
  assert.equal(formatBalanceWatts(0), "0 W");
  assert.equal(balanceDirection(positive!), "Reported supply exceeds consumption");
  assert.equal(balanceDirection(negative!), "Reported consumption exceeds supply");
  assert.equal(calculatePowerBalance(reading({ solarProduction: 2147483647, gridConsumption: 2147483647, batteryPower: 2147483647, loadPower: 0 })), 6442450941);
});

test("missing or invalid required power values remain unavailable, while a reported zero is preserved", () => {
  assert.equal(calculatePowerBalance(null), null);
  for (const field of ["solarProduction", "gridConsumption", "batteryPower", "loadPower"])
    for (const value of [undefined, null, Number.NaN, Infinity])
      assert.equal(calculatePowerBalance(reading({ [field]: value })), null);
  assert.equal(calculatePowerBalance(reading({ solarProduction: -1 })), null);
  assert.equal(calculatePowerBalance(reading({ loadPower: -1 })), null);
  assert.equal(calculatePowerBalance(reading({ solarProduction: 0, gridConsumption: 0, batteryPower: 0, loadPower: 0 })), 0);
});

test("a stale, future or invalid poll cannot support a reported balance; offsetless API dates are UTC", () => {
  assert.deepEqual(reportedPowerBalance(reading(), now), { watts: 0, reason: null });
  assert.deepEqual(reportedPowerBalance(reading({ timestamp: "2026-10-01T10:00:00" }), now), { watts: 0, reason: null });
  for (const invalidTime of ["not-a-date", "2026-10-01T09:50:00Z", "2026-10-01T10:02:00Z"])
    assert.equal(reportedPowerBalance(reading({ timestamp: invalidTime }), now).watts, null);
  assert.equal(reportedPowerBalance(reading({ batteryPower: Number.NaN }), now).watts, null);
});

test("provided source metadata must establish recent aligned solar and grid readings from the same inverter", () => {
  const measured = { solarObservedAt: timestamp, gridObservedAt: timestamp, solarDeviceSn: "inverter-a", gridDeviceSn: "inverter-a" };
  assert.equal(reportedPowerBalance(reading(measured), now).watts, 0);
  for (const changes of [
    { solarObservedAt: null }, { gridObservedAt: null }, { solarDeviceSn: "" }, { gridDeviceSn: "inverter-b" },
    { solarObservedAt: "2026-10-01T09:57:00Z" }, { gridObservedAt: "2026-10-01T09:49:00Z" },
    { solarObservedAt: "2026-10-01T10:02:00Z" }, { gridObservedAt: "not-a-date" }
  ]) assert.equal(reportedPowerBalance(reading({ ...measured, ...changes }), now).watts, null);
  assert.equal(reportedPowerBalance(reading({ solarObservedAt: null, gridObservedAt: null, solarDeviceSn: null, gridDeviceSn: null }), now).watts, null);
  assert.equal(reportedPowerBalance(reading(), now).watts, 0); // Backward-compatible poll-only estimate is explicit in the UI.
});

test("explicit invalid power flags hide default zeros while real zero and legacy readings remain available", () => {
  const zero = reading({ solarProduction: 0, gridConsumption: 0, batteryPower: 0, loadPower: 0 });
  for (const key of ["batteryPowerValid", "loadPowerValid", "gridPowerValid", "solarPowerValid"]) {
    assert.equal(calculatePowerBalance({ ...zero, [key]: false }), null);
    assert.equal(reportedPowerBalance({ ...zero, [key]: false }, now).watts, null);
    assert.equal(calculatePowerBalance({ ...zero, [key]: true }), 0);
    assert.equal(calculatePowerBalance({ ...zero, [key]: null }), 0);
  }
  assert.deepEqual(batteryFlow(0, false), { label: "Battery power", watts: null });
  assert.deepEqual(batteryFlow(0, true), { label: "Battery idle", watts: 0 });
  assert.deepEqual(batteryFlow(0, null), { label: "Battery idle", watts: 0 });
  assert.equal(zero.batteryPower, 0);
  assert.equal(zero.loadPower, 0);
});
