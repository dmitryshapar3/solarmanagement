import assert from "node:assert/strict";
import { test } from "node:test";
import React from "react";
import { act, create } from "react-test-renderer";
import { DEFAULT_ROOF_CAMERA, diagramRoofs, normalizeRoofCamera, orbitRoofGesture, projectRoofPoint, roofMesh, sunDay, sunPoint3, sunPosition, type Point3, type RoofSunSite } from "../src/ui/forms/roofSunGeometry";
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
test("native 3D SVG redraws its sun and connected roof directly from an unsaved draft", async () => {
  globals.IS_REACT_ACT_ENVIRONMENT = true; let renderer: ReturnType<typeof create> | undefined;
  try {
    const RoofSunDiagram = (await uiHarness('export { RoofSunDiagram } from "./src/ui/forms/RoofSunDiagram";', { stubComponents: true })).RoofSunDiagram!;
    const at = Date.parse("2026-10-09T10:00:00Z");
    await act(async () => { renderer = create(React.createElement(RoofSunDiagram, { site, at })); });
    const before = renderer!.root.findByProps({ testID: "roof-sun-path" }).props.d;
    assert.equal(renderer!.root.findAllByProps({ testID: "roof-sun-direction" }).length, 2);
    const roofBefore = renderer!.root.findByProps({ testID: "roof-sun-face-roof-1" }).props.d;
    assert.equal(renderer!.root.findAllByProps({ testID: "roof-sun-ridge" }).length, 1);
    await act(async () => { renderer!.update(React.createElement(RoofSunDiagram, { site: { ...site, latitude: -33.8688, roof1Azimuth: 270, roof1Tilt: 60, roof2Kwp: 0 }, at })); });
    assert.notEqual(renderer!.root.findByProps({ testID: "roof-sun-path" }).props.d, before);
    assert.notEqual(renderer!.root.findByProps({ testID: "roof-sun-face-roof-1" }).props.d, roofBefore);
    assert.equal(renderer!.root.findAllByProps({ testID: "roof-sun-ridge" }).length, 0);
    assert.equal(renderer!.root.findAllByProps({ testID: "roof-sun-roof-2" }).length, 0);
    assert.match(renderer!.root.findByProps({ testID: "roof-sun-orbit" }).props.accessibilityLabel, /Azimuth 270\.0°.*Tilt 60\.0°/);
    await act(async () => { renderer!.update(React.createElement(RoofSunDiagram, { site: { ...site, latitude: NaN }, at })); });
    assert.equal(renderer!.root.findAllByProps({ testID: "roof-sun-path" }).length, 0);
    assert.ok(renderer!.root.findAllByType("Text").some(node => String(node.props.children).includes("Enter valid coordinates")));
  } finally { await act(async () => renderer?.unmount()); delete globals.IS_REACT_ACT_ENVIRONMENT; }
});

const dot = (a: Point3, b: Point3) => a.x * b.x + a.y * b.y + a.z * b.z;
const subtract = (a: Point3, b: Point3): Point3 => ({ x: a.x - b.x, y: a.y - b.y, z: a.z - b.z });
const close = (a: Point3, b: Point3, tolerance = 1e-7) => Math.hypot(a.x - b.x, a.y - b.y, a.z - b.z) < tolerance;

test("opposing slopes share one real ridge and walls meet both roof faces without gaps", () => {
  const mesh = roofMesh(site), faces = mesh.faces.filter(face => face.kind === "roof");
  assert.equal(faces.length, 2); assert.equal(mesh.ridge.length, 1); assert.equal(mesh.ridge[0]!.length, 2);
  for (const endpoint of mesh.ridge[0]!) for (const face of faces) assert.ok(face.points.some(point => close(point, endpoint)));
  for (const face of mesh.faces.filter(face => face.kind === "wall")) for (const top of face.points.slice(2)) assert.ok(faces.some(roof => roof.points.some(point => close(point, top))));
  const ridge = subtract(mesh.ridge[0]![1]!, mesh.ridge[0]![0]!);
  assert.ok(Math.abs(dot(ridge, { x: Math.sin(230 * Math.PI / 180), y: Math.cos(230 * Math.PI / 180), z: 0 })) < 1e-7);
  assert.ok(Math.abs(Math.hypot(ridge.x, ridge.y, ridge.z) - 80 * mesh.scale) < 1e-7);
});

test("arbitrary unequal slopes remain the true lower envelope on a single footprint", () => {
  const draft = { ...site, roof1Tilt: 18, roof1Azimuth: 40, roof2Tilt: 48, roof2Azimuth: 140 }, mesh = roofMesh(draft);
  const slopes = [1, 2].map(number => { const tilt = (number === 1 ? draft.roof1Tilt : draft.roof2Tilt) * Math.PI / 180, az = (number === 1 ? draft.roof1Azimuth : draft.roof2Azimuth) * Math.PI / 180; return { x: Math.sin(az) * Math.tan(tilt), y: Math.cos(az) * Math.tan(tilt), z: 0 }; });
  const corners = mesh.footprint.map(p => ({ x: p.x / mesh.scale, y: p.y / mesh.scale, z: 0 })), height = 14 + Math.max(...slopes.flatMap(slope => corners.map(p => Math.abs(dot(slope, p)))));
  for (const face of mesh.faces.filter(face => face.kind === "roof")) {
    for (const p of face.points) {
      const original = { x: p.x / mesh.scale, y: p.y / mesh.scale, z: p.z / mesh.scale };
      assert.ok(Math.abs(original.z - Math.min(...slopes.map(slope => height - dot(slope, original)))) < 1e-7);
      assert.ok(original.z >= 14 - 1e-7);
      assert.ok(Math.abs(dot(face.normal, subtract(p, face.points[0]!))) < 1e-7);
    }
    const expectedTilt = face.roofNumber === 1 ? 18 : 48;
    assert.ok(Math.abs(Math.acos(face.normal.z) * 180 / Math.PI - expectedTilt) < 1e-7);
  }
});

test("flat, coplanar, steep and vertical configurations remain finite without clamping roof angles", () => {
  for (const tilt of [0, 25, 89, 89.999, 90]) {
    const mesh = roofMesh({ ...site, roof1Tilt: tilt, roof2Tilt: tilt, roof2Azimuth: site.roof1Azimuth });
    assert.ok(mesh.faces.every(face => face.points.length >= 3 && [...face.points, ...face.panels, ...face.grid.flat(), face.normal].every(p => [p.x, p.y, p.z].every(Number.isFinite))));
    assert.ok(Math.max(...mesh.faces.flatMap(face => face.points.map(p => p.z))) <= 112 + 1e-7);
    assert.equal(mesh.hasVerticalPanels, tilt === 90);
    for (const face of mesh.faces.filter(face => face.roofNumber)) {
      assert.ok(Math.abs(Math.acos(face.normal.z) * 180 / Math.PI - tilt) < 1e-7);
      assert.ok(face.points.some(p => Math.hypot(p.x - face.points[0]!.x, p.y - face.points[0]!.y, p.z - face.points[0]!.z) > 1e-8));
    }
    if (tilt === 90) { assert.equal(mesh.faces.filter(face => face.kind === "vertical").length, 2); assert.ok(mesh.faces.some(face => face.id === "flat-base")); }
  }
  assert.deepEqual(roofMesh({ ...site, roof1Azimuth: 0, roof2Kwp: 0 }), roofMesh({ ...site, roof1Azimuth: 360, roof2Kwp: 0 }));
  assert.equal(roofMesh({ ...site, roof1Kwp: 0, roof2Kwp: 0 }).faces.length, 0);
  assert.equal(roofMesh({ ...site, roof1Tilt: NaN, roof2Azimuth: 361 }).faces.length, 0);
});

test("orthographic camera preserves physical directions, depth and proportional zoom", () => {
  const camera = { yaw: 0, elevation: 30, zoom: 1 }, origin = projectRoofPoint({ x: 0, y: 0, z: 0 }, camera), east = projectRoofPoint({ x: 20, y: 0, z: 0 }, camera), north = projectRoofPoint({ x: 0, y: 20, z: 0 }, camera), above = projectRoofPoint({ x: 0, y: 0, z: 20 }, camera);
  assert.ok(east.x > origin.x && north.y > origin.y && north.z > origin.z && above.y < origin.y && above.z > origin.z);
  const zoomed = projectRoofPoint({ x: 20, y: 15, z: 12 }, { ...camera, zoom: 2 }), normal = projectRoofPoint({ x: 20, y: 15, z: 12 }, camera);
  assert.ok(Math.abs(zoomed.x - origin.x - 2 * (normal.x - origin.x)) < 1e-7 && Math.abs(zoomed.y - origin.y - 2 * (normal.y - origin.y)) < 1e-7);
  assert.ok(close(sunPoint3({ azimuth: 90, elevation: 0 }), { x: 110, y: 0, z: 0 }));
  assert.ok(close(sunPoint3({ azimuth: 0, elevation: 90 }), { x: 0, y: 0, z: 110 }));
});

test("orbit clamps its camera and pinch never introduces rotation or touch-count jumps", () => {
  assert.deepEqual(normalizeRoofCamera({ yaw: 725, elevation: 100, zoom: 9 }), { yaw: 5, elevation: 85, zoom: 2.5 });
  assert.deepEqual(normalizeRoofCamera({ yaw: NaN, elevation: -2, zoom: -1 }), { yaw: -35, elevation: 10, zoom: .7 });
  const one = [{ pageX: 10, pageY: 20 }], two = [...one, { pageX: 110, pageY: 20 }];
  assert.deepEqual(orbitRoofGesture({ ...DEFAULT_ROOF_CAMERA }, one, two), DEFAULT_ROOF_CAMERA);
  const pinched = orbitRoofGesture({ ...DEFAULT_ROOF_CAMERA }, two, [one[0]!, { pageX: 210, pageY: 20 }]);
  assert.equal(pinched.zoom, 2); assert.equal(pinched.yaw, -35); assert.equal(pinched.elevation, 32);
  assert.deepEqual(orbitRoofGesture(pinched, two, one), pinched);
  assert.deepEqual(orbitRoofGesture(pinched, one, [{ pageX: NaN, pageY: 20 }]), pinched);
  const orbited = orbitRoofGesture(pinched, one, [{ pageX: 40, pageY: -1000 }]); assert.equal(orbited.elevation, 85); assert.equal(orbited.zoom, 2); assert.notEqual(orbited.yaw, pinched.yaw);
});

test("native gestures, accessible controls, draft updates and cancellation share one camera", async () => {
  globals.IS_REACT_ACT_ENVIRONMENT = true; let renderer: ReturnType<typeof create> | undefined;
  try {
    const RoofSunDiagram = (await uiHarness('export { RoofSunDiagram } from "./src/ui/forms/RoofSunDiagram";', { stubComponents: true })).RoofSunDiagram!;
    const at = Date.parse("2026-10-09T10:00:00Z"); await act(async () => { renderer = create(React.createElement(RoofSunDiagram, { site, at })); });
    const scene = () => renderer!.root.findByProps({ testID: "roof-sun-orbit" });
    const event = (points: [number, number][]) => ({ nativeEvent: { touches: points.map(([pageX, pageY]) => ({ pageX, pageY })) } });
    const initialPath = renderer!.root.findByProps({ testID: "roof-sun-path" }).props.d;
    assert.equal(scene().props.onStartShouldSetResponder(), true); assert.equal(scene().props.onResponderTerminationRequest(), false);
    assert.equal(renderer!.root.findByProps({ testID: "roof-sun-diagram" }).props.onStartShouldSetResponder, undefined);
    await act(async () => { scene().props.onResponderGrant(event([[0, 0]])); scene().props.onResponderMove(event([[35, 10]])); });
    assert.notEqual(renderer!.root.findByProps({ testID: "roof-sun-path" }).props.d, initialPath);
    await act(async () => { scene().props.onResponderStart(event([[35, 10], [135, 10]])); scene().props.onResponderMove(event([[35, 10], [235, 10]])); });
    assert.equal(scene().props.accessibilityValue.now, 200);
    await act(async () => { scene().props.onResponderEnd(event([[35, 10]])); scene().props.onResponderMove(event([[35, 10]])); });
    assert.equal(scene().props.accessibilityValue.now, 200);
    await act(async () => { renderer!.update(React.createElement(RoofSunDiagram, { site: { ...site, roof1Tilt: 40 }, at })); });
    assert.equal(scene().props.accessibilityValue.now, 200);
    await act(async () => { scene().props.onResponderTerminate(); scene().props.onResponderMove(event([[100, 100]])); });
    assert.equal(scene().props.accessibilityValue.now, 200);
    await act(async () => { scene().props.onAccessibilityAction({ nativeEvent: { actionName: "increment" } }); });
    assert.equal(scene().props.accessibilityValue.now, 220);
    for (const label of ["Rotate left", "Rotate right", "Tilt view up", "Tilt view down", "Zoom in", "Zoom out", "Reset view"]) assert.equal(renderer!.root.findByProps({ accessibilityLabel: label }).props.accessibilityRole, "button");
    await act(async () => { renderer!.root.findByProps({ accessibilityLabel: "Reset view" }).props.onPress(); });
    assert.equal(scene().props.accessibilityValue.now, 100);
    assert.equal(renderer!.root.findByProps({ testID: "roof-sun-path" }).props.d, initialPath);
  } finally { await act(async () => renderer?.unmount()); delete globals.IS_REACT_ACT_ENVIRONMENT; }
});

test("each configured module is a whole 1:1.7 rectangle inside the true sloping facet, including1000 modules", () => {
  for (const count of [1, 7, 8, 17, 1000]) for (const tilt of [0, 25, 89.999, 90]) {
    const mesh = roofMesh({ ...site, roof1PanelCount: count, roof1PanelsPerRow: 0, roof1Tilt: tilt, roof2Kwp: 0 });
    const face = mesh.faces.find(item => item.roofNumber === 1)!;
    assert.equal(face.panelTiles.length, count); assert.equal(face.panels.length, 0);
    const centroid = { x: face.points.reduce((s, p) => s + p.x, 0) / face.points.length, y: face.points.reduce((s, p) => s + p.y, 0) / face.points.length, z: face.points.reduce((s, p) => s + p.z, 0) / face.points.length };
    for (const tile of face.panelTiles) {
      assert.equal(tile.length, 4);
      const width = Math.hypot(...Object.values(subtract(tile[1]!, tile[0]!))), height = Math.hypot(...Object.values(subtract(tile[3]!, tile[0]!)));
      assert.ok(width > 0 && Number.isFinite(width) && Math.abs(height / width - 1.7) < 1e-5);
      for (const p of tile) {
        assert.ok([p.x, p.y, p.z].every(Number.isFinite));
        assert.ok(Math.abs(dot(face.normal, subtract(p, face.points[0]!)) - .3 * mesh.scale) < 1e-7);
        for (let i = 0; i < face.points.length; ++i) {
          const a = face.points[i]!, edge = subtract(face.points[(i + 1) % face.points.length]!, a);
          const side = (q: Point3) => { const d = subtract(q, a); return dot({ x: edge.y * d.z - edge.z * d.y, y: edge.z * d.x - edge.x * d.z, z: edge.x * d.y - edge.y * d.x }, face.normal); };
          assert.ok(side(p) * side(centroid) >= -1e-7, "Whole module must stay inside every facet edge");
        }
      }
    }
  }
});

test("chosen rows preserve exactcount, regulargaps and centered incomplete rows even on a clipped facet", () => {
  for (const [count, columns] of [[7, 4], [8, 4], [17, 6]] as const) {
    const mesh = roofMesh({ ...site, roof1PanelCount: count, roof1PanelsPerRow: columns, roof1Azimuth: 40, roof1Tilt: 18, roof2Azimuth: 140, roof2Tilt: 48 }), face = mesh.faces.find(item => item.roofNumber === 1)!;
    assert.equal(face.panelTiles.length, count);
    const az = 40 * Math.PI / 180, u = { x: Math.cos(az), y: -Math.sin(az), z: 0 }, centers = face.panelTiles.map(tile => tile.reduce((s, p) => s + dot(p, u) / 4, 0));
    const rows = Array.from({ length: Math.ceil(count / columns) }, (_, index) => centers.slice(index * columns, (index + 1) * columns));
    const average = (values: number[]) => values.reduce((s, v) => s + v, 0) / values.length;
    for (const row of rows) assert.ok(Math.abs(average(row) - average(rows[0]!)) < 1e-7, "Incomplete final row shares the same center");
    const first = face.panelTiles[0]!, moduleWidth = Math.hypot(...Object.values(subtract(first[1]!, first[0]!)));
    assert.ok(Math.abs((centers[1]! - centers[0]!) / moduleWidth - 1.15) < 1e-7);
  }
});

test("unknown, zero and invalid counts never invent panels from kWp or a fake PV grid", () => {
  for (const count of [undefined, null, 0, -1, 1.5, 1001, Number.NaN, Infinity]) {
    const mesh = roofMesh({ ...site, roof1PanelCount: count, roof2PanelCount: count, roof1Kwp: 9000 });
    assert.ok(mesh.faces.every(face => face.panelTiles.length === 0 && face.panels.length === 0));
    assert.ok(mesh.faces.filter(face => face.kind === "roof").every(face => face.grid.length === 0));
  }
  const a = roofMesh({ ...site, roof1PanelCount: 7, roof2PanelCount: 8 }), b = roofMesh({ ...site, roof1PanelCount: 7, roof2PanelCount: 8, roof1Kwp: 999 });
  assert.deepEqual(a.faces.map(face => face.panelTiles), b.faces.map(face => face.panelTiles), "Capacity cannot infer or alter known panel placement");
});

test("native individual panels redraw on count/row drafts while retaining orbit and showing unknowncount hint", async () => {
  globals.IS_REACT_ACT_ENVIRONMENT = true; let renderer: ReturnType<typeof create> | undefined;
  try {
    const RoofSunDiagram = (await uiHarness('export { RoofSunDiagram } from "./src/ui/forms/RoofSunDiagram";', { stubComponents: true })).RoofSunDiagram!;
    const at = Date.parse("2026-10-09T10:00:00Z"), props = { ...site, roof2Kwp: 0, roof1PanelCount: 7, roof1PanelsPerRow: 4 };
    await act(async () => { renderer = create(React.createElement(RoofSunDiagram, { site: props, at })); });
    const tiles = () => renderer!.root.findAll(node => typeof node.props.testID === "string" && node.props.testID.startsWith("roof-sun-panel-"));
    assert.equal(tiles().length, 7); const before = tiles()[0]!.props.d;
    const label = renderer!.root.findByProps({ testID: "roof-sun-label-1" });
    const maximumY = Math.max(...roofMesh(props).faces.find(face => face.roofNumber === 1)!.points.map(point => projectRoofPoint(point, DEFAULT_ROOF_CAMERA).y));
    const labelY = Number(label.props.transform.match(/translate\([^ ]+ ([^)]+)\)/)[1]);
    assert.ok(Math.abs(labelY - maximumY - 12) < 1e-7, "Roof badge stays below the facet rather than obscuring modules");
    const svgChildren = renderer!.root.findByType("Svg").children;
    assert.ok(svgChildren.indexOf(label) > svgChildren.findIndex(child => typeof child !== "string" && child.props.testID === "roof-sun-roof-1"), "Badges draw after opaque surfaces");
    await act(async () => { renderer!.root.findByProps({ accessibilityLabel: "Zoom in" }).props.onPress(); renderer!.update(React.createElement(RoofSunDiagram, { site: { ...props, roof1PanelCount: 17, roof1PanelsPerRow: 6 }, at })); });
    assert.equal(tiles().length, 17); assert.notEqual(tiles()[0]!.props.d, before); assert.equal(renderer!.root.findByProps({ testID: "roof-sun-orbit" }).props.accessibilityValue.now, 120);
    await act(async () => renderer!.update(React.createElement(RoofSunDiagram, { site: { ...props, roof1PanelCount: null }, at })));
    assert.equal(tiles().length, 0); assert.ok(renderer!.root.findAllByType("Text").some(node => node.props.children === "Enter the panel count for each roof to show its panels."));
  } finally { await act(async () => renderer?.unmount()); delete globals.IS_REACT_ACT_ENVIRONMENT; }
});
