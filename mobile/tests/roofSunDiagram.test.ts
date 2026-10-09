import assert from "node:assert/strict";
import { test } from "node:test";
import React from "react";
import { act, create } from "react-test-renderer";
import { diagramRoofs, sunDay, sunPosition, type RoofSunSite } from "../src/ui/forms/roofSunGeometry";
import { globals, uiHarness } from "./support/uiHarness";

const site: RoofSunSite = { latitude: 50.095278, longitude: 20.070278, timeZoneId: "Europe/Warsaw", roof1Kwp: 4.32, roof1Tilt: 25, roof1Azimuth: 230, roof2Kwp: 3.78, roof2Tilt: 25, roof2Azimuth: 50 };
test("NOAA approximate position agrees with NREL SPA reference within 0.3 degrees", () => {
  const point = sunPosition(39.742476, -105.1786, Date.parse("2003-10-17T19:30:30Z"));
  // pvlib's NREL SPA reference fixture: geometric elevation, without atmospheric refraction.
  assert.ok(Math.abs(point.elevation - 39.872046) < .3); assert.ok(Math.abs(point.azimuth - 194.34024) < .3);
});
test("local solar calendar covers 23 and 25 hour days without changing timezone", () => {
  for (const [instant, hours] of [["2026-03-29T12:00:00Z", 23], ["2026-10-25T12:00:00Z", 25]] as const) {
    const day = sunDay(site.latitude, site.longitude, site.timeZoneId, Date.parse(instant))!;
    assert.equal((day.endsAt - day.startsAt) / 3600000, hours); assert.equal(day.state, "normal");
    assert.ok(day.sunrise! >= day.startsAt && day.sunrise! <= day.endsAt && day.sunset! > day.sunrise! && day.sunset! <= day.endsAt);
    assert.equal(day.paths.length, 1); const path = day.paths[0]!;
    assert.ok(Math.abs(path[0]!.elevation) < .00001 && Math.abs(path.at(-1)!.elevation) < .00001);
    for (const point of path) assert.ok(Number.isFinite(point.x) && point.x >= 48 && point.x <= 272 && Number.isFinite(point.y) && point.y >= 48 && point.y <= 272);
  }
});
test("sun path uses site date, actual longitude, and the correct hemisphere", () => {
  const instant = Date.parse("2026-01-01T00:30:00Z");
  assert.equal(sunDay(21.3, -157.8, "Pacific/Honolulu", instant)!.date, "2025-12-31");
  assert.equal(sunDay(-36.8, 174.7, "Pacific/Auckland", instant)!.date, "2026-01-01");
  const south = sunPosition(-33.8688, 151.2093, Date.parse("2026-01-15T01:00:00Z"));
  assert.ok(south.elevation > 60 && south.y < 160);
  assert.notEqual(sunPosition(50, 0, instant).azimuth, sunPosition(50, 30, instant).azimuth);
});
test("polar day and night show no invented rise/set and invalid inputs show no sun", () => {
  for (const [latitude, instant, state] of [[78, "2026-06-21T12:00:00Z", "polar-day"], [78, "2026-12-21T12:00:00Z", "polar-night"], [-78, "2026-06-21T12:00:00Z", "polar-night"], [-78, "2026-12-21T12:00:00Z", "polar-day"]] as const) {
    const day = sunDay(latitude, 15, "UTC", Date.parse(instant))!;
    assert.equal(day.state, state); assert.equal(day.sunrise, null); assert.equal(day.sunset, null);
    assert.equal(day.now !== null, state === "polar-day"); assert.equal(day.paths.length > 0, state === "polar-day");
  }
  for (const [latitude, longitude, zone] of [[NaN, 20, "UTC"], [91, 20, "UTC"], [50, Infinity, "UTC"], [50, 181, "UTC"], [50, 20, "not/a-zone"]] as const) assert.equal(sunDay(latitude, longitude, zone), null);
});
test("capacity, bearing and tilt produce live roof geometry while unused or invalid groups disappear", () => {
  const roofs = diagramRoofs({ ...site, roof2Tilt: 90 }); assert.equal(roofs.length, 2);
  assert.equal(roofs[0]!.azimuth, 230); assert.equal(roofs[1]!.azimuth, 50);
  assert.ok(roofs[0]!.depth > roofs[1]!.depth && roofs[0]!.width > roofs[1]!.width);
  const one = diagramRoofs({ ...site, roof1Kwp: 0, roof2Tilt: 0, roof2Azimuth: 360 });
  assert.equal(one.length, 1); assert.equal(one[0]!.number, 2); assert.equal(one[0]!.azimuth, 0); assert.equal(one[0]!.centerX, 160);
  assert.equal(diagramRoofs({ ...site, roof1Tilt: NaN, roof2Azimuth: 361 }).length, 0);
});
test("native SVG redraws its path, direction, tilt and metadata directly from an unsaved draft", async () => {
  globals.IS_REACT_ACT_ENVIRONMENT = true; let renderer: ReturnType<typeof create> | undefined;
  try {
    const RoofSunDiagram = (await uiHarness('export { RoofSunDiagram } from "./src/ui/forms/RoofSunDiagram";', { stubComponents: true })).RoofSunDiagram!;
    const at = Date.parse("2026-10-09T10:00:00Z");
    await act(async () => { renderer = create(React.createElement(RoofSunDiagram, { site, at })); });
    const before = renderer!.root.findByProps({ testID: "roof-sun-path" }).props.d;
    assert.equal(renderer!.root.findAllByProps({ testID: "roof-sun-direction" }).length, 2);
    assert.equal(renderer!.root.findByProps({ testID: "roof-sun-bearing-1" }).props.rotation, 230);
    assert.equal(renderer!.root.findByProps({ testID: "roof-sun-profile-1" }).props.transform, "translate(20 60) rotate(-25)");
    await act(async () => { renderer!.update(React.createElement(RoofSunDiagram, { site: { ...site, latitude: -33.8688, roof1Azimuth: 90, roof1Tilt: 60, roof2Kwp: 0 }, at })); });
    assert.notEqual(renderer!.root.findByProps({ testID: "roof-sun-path" }).props.d, before);
    assert.equal(renderer!.root.findByProps({ testID: "roof-sun-bearing-1" }).props.rotation, 90);
    assert.equal(renderer!.root.findByProps({ testID: "roof-sun-profile-1" }).props.transform, "translate(20 60) rotate(-60)");
    assert.equal(renderer!.root.findAllByProps({ testID: "roof-sun-roof-2" }).length, 0);
    assert.match(renderer!.root.findAllByType("Svg")[0]!.props.accessibilityLabel, /Azimuth 90\.0°/);
    await act(async () => { renderer!.update(React.createElement(RoofSunDiagram, { site: { ...site, latitude: NaN }, at })); });
    assert.equal(renderer!.root.findAllByProps({ testID: "roof-sun-path" }).length, 0);
    assert.ok(renderer!.root.findAllByType("Text").some(node => String(node.props.children).includes("Enter valid coordinates")));
  } finally { await act(async () => renderer?.unmount()); delete globals.IS_REACT_ACT_ENVIRONMENT; }
});
