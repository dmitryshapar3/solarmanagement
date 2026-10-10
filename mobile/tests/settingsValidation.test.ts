import assert from "node:assert/strict";
import { test } from "node:test";
import { panelCountError, panelsPerRowError, parseSettingNumber, settingBearingError, settingContractDateError, settingNumberError, settingTimeZoneError } from "../src/features/settings/formValidation";
import { compassPoint } from "../src/ui/forms/compassPolicy";
test("site coordinate fields accept decimal comma and retain invalid partial numbers for field validation", () => {
  assert.equal(parseSettingNumber(" -52,31 "), -52.31);
  assert.ok(Number.isNaN(parseSettingNumber("-")));
  assert.ok(Number.isNaN(parseSettingNumber("")));
  assert.equal(settingNumberError(parseSettingNumber("-52,31"), -90, 90), null);
  assert.ok(settingNumberError(parseSettingNumber("-"), -90, 90));
  assert.ok(settingNumberError(91, -90, 90));
  assert.ok(settingNumberError(360, 0, 359));
  assert.equal(settingNumberError(0, 0, 1000), null);
});
test("bearing directions follow clockwise north and reject invalid new values while preserving stored north360", () => {
  for (const [bearing, expected] of [[0, {x:72,y:50}], [90, {x:94,y:72}], [180, {x:72,y:94}], [270, {x:50,y:72}], [360, {x:72,y:50}]] as const) {
    const point = compassPoint(bearing)!; assert.ok(Math.abs(point.x - expected.x) < 1e-9); assert.ok(Math.abs(point.y - expected.y) < 1e-9);
  }
  for (const bearing of [Number.NaN, Infinity, -1, 361]) assert.equal(compassPoint(bearing), null);
  assert.ok(settingBearingError(360)); assert.equal(settingBearingError(360,360),null); assert.ok(settingBearingError(360,180)); assert.equal(settingBearingError(359),null);
});
test("contract calendar dates and time zones are validated before installation header save", () => {
  for (const date of ["", "1999-12-31", "2026-02-30", "2026-1-01", "invalid"]) assert.ok(settingContractDateError(date));
  assert.equal(settingContractDateError("2000-01-01"),null); assert.equal(settingContractDateError("2028-02-29"),null);
  assert.equal(settingTimeZoneError("Europe/Warsaw"),null); assert.equal(settingTimeZoneError("UTC"),null);
  assert.ok(settingTimeZoneError("")); assert.ok(settingTimeZoneError("Unknown/Zone"));
});
test("polling interval is finite, integral and bounded before saving", () => {
  for (const value of [Number.NaN, Infinity, 0, 3601, 30.5]) assert.ok(settingNumberError(value, 1, 3600, true));
  for (const value of [1, 30, 3600]) assert.equal(settingNumberError(value, 1, 3600, true), null);
});
test("optional panel counts and row sizes require bounded integers and validate known positive counts", () => {
  for (const value of [undefined, null, 0, 1, 1000]) assert.equal(panelCountError(value), null);
  for (const value of [-1, 1.5, 1001, Number.NaN, Infinity]) { assert.ok(panelCountError(value)); assert.ok(panelsPerRowError(value)); }
  for (const [value, count] of [[undefined, 7], [null, 7], [0, 7], [7, 7], [1000, null], [1000, 0]] as const) assert.equal(panelsPerRowError(value, count), null);
  assert.ok(panelsPerRowError(8, 7));
});
