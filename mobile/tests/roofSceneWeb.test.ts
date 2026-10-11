import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";

type EventLike = Record<string, any>;
type Listener = (event: EventLike) => void;
class DomNode {
  children: DomNode[] = [];
  attributes = new Map<string, string>();
  listeners = new Map<string, Set<Listener>>();
  style = { touchAction: "pan-y" };
  dataset: Record<string, string> = {};
  disabled = false;
  textContent = "";
  captured = new Set<number>();
  classes = new Set<string>();
  classList = { add: (name: string) => this.classes.add(name), remove: (name: string) => this.classes.delete(name) };
  constructor(readonly name: string, readonly ownerDocument: DomDocument) {}
  setAttribute(name: string, value: string) { this.attributes.set(name, value); }
  appendChild(child: DomNode) { if (child.name === "fragment") this.children.push(...child.children); else this.children.push(child); return child; }
  replaceChildren(...children: DomNode[]) { this.children = []; for (const child of children) this.appendChild(child); }
  addEventListener(name: string, listener: Listener) { const listeners = this.listeners.get(name) ?? new Set<Listener>(); listeners.add(listener); this.listeners.set(name, listeners); }
  removeEventListener(name: string, listener: Listener) { this.listeners.get(name)?.delete(listener); }
  contains(node: DomNode): boolean { return node === this || this.children.some(child => child.contains(node)); }
  querySelector(selector: string): DomNode | null { return this.children.find(child => selector === "svg.roof-scene-interactive" && child.name === "svg" || selector === `[data-roof-action="${child.dataset.roofAction}"]`) ?? null; }
  closest(selector: string) { return selector === "[data-roof-action]" && this.dataset.roofAction ? this : null; }
  setPointerCapture(id: number) { this.captured.add(id); }
  hasPointerCapture(id: number) { return this.captured.has(id); }
  releasePointerCapture(id: number) { this.captured.delete(id); }
  fire(name: string, values: EventLike = {}) {
    const event: EventLike = { target: this, button: 0, pointerId: 1, clientX: 0, clientY: 0, prevented: false, stopped: false,
      preventDefault() { this.prevented = true; }, stopPropagation() { this.stopped = true; }, ...values };
    for (const listener of [...this.listeners.get(name) ?? []]) listener(event);
    return event;
  }
}
class DomDocument {
  frames = new Map<number, FrameRequestCallback>();
  requests = 0;
  defaultView = { requestAnimationFrame: (callback: FrameRequestCallback) => { const id = ++this.requests; this.frames.set(id, callback); return id; }, cancelAnimationFrame: (id: number) => this.frames.delete(id) };
  createElementNS(_namespace: string, name: string) { return new DomNode(name, this); }
  createDocumentFragment() { return new DomNode("fragment", this); }
  flush() { const frames = [...this.frames.values()]; this.frames.clear(); for (const frame of frames) frame(0); }
}
type Renderer = { rotateSolarPoint(point: number[], frontYaw: number, angles: { x: number; y: number; z: number }): number[]; mount(root: any, scene: any): void; update(root: any, scene: any): void; dispose(root: any): void;
  configureOrientation(root: any, enabled: boolean, disabled: boolean, bridge: any, bearing: number): void;
  updateOrientation(root: any, scene: any, enabled: boolean, disabled: boolean, bridge: any, bearing: number): void };
const renderer: Promise<Renderer> = readFile(new URL("../../src/DeyeSolar.Web/wwwroot/js/roof-scene.js", import.meta.url), "utf8")
  .then(source => import(`data:text/javascript;base64,${Buffer.from(source).toString("base64")}`));
const scene = () => ({ faces: [{ kind: "roof", roof: 1, points: [[0, 0, 10], [10, 0, 10], [0, 10, 10]] }],
  ground: [[-20, -20, 0], [20, -20, 0], [20, 20, 0]], axes: [[[0, 0, 0], [10, 0, 0]]],
  frontYaw: -35, lines: [{ kind: "ridge", points: [[0, 0, 10], [10, 0, 10]] }], sunPaths: [[[0, 100, 0], [0, 80, 30], [0, 60, 60], [0, 30, 80], [0, 0, 100], [0, -30, 80]]],
  sunNow: [0, 0, 100], crossings: [[0, 100, 0]], labels: [{ text: "N", kind: "cardinal", point: [0, 100, 0] }] });
function fixture() {
  const document = new DomDocument(), root = new DomNode("div", document), svg = new DomNode("svg", document);
  root.children.push(svg);
  const button = (action: string) => { const node = new DomNode("button", document); node.dataset.roofAction = action; root.children.push(node); return node; };
  const camera = () => ({ yaw: Number(svg.attributes.get("data-yaw")), elevation: Number(svg.attributes.get("data-elevation")), zoom: Number(svg.attributes.get("data-zoom")) });
  return { document, root, svg, button, camera };
}

test("web roof renderer uses ENU orthographic projection and draws near faces last without HTML injection", async () => {
  const { mount, dispose } = await renderer, { root, svg } = fixture(), value = scene();
  value.faces.unshift({ kind: "roof", roof: 2, points: [[0, 0, -10], [10, 0, -10], [0, 10, -10]] });
  value.labels[0]!.text = "<img src=x onerror=alert(1)>";
  mount(root, value);
  assert.ok(root.classes.has("scene-ready"));
  const faces = svg.children.filter(node => node.attributes.get("class")?.includes("roof-scene-face"));
  assert.deepEqual(faces.map(node => node.attributes.get("data-roof")), ["2", "1"]);
  const first = faces[1]!.attributes.get("points")!.split(" ")[0]!.split(",").map(Number);
  assert.equal(first[0], 180);
  assert.ok(Math.abs(first[1]! - (180 - 10 * Math.cos(32 * Math.PI / 180))) < .001);
  assert.equal(svg.children.at(-1)!.name, "text");
  assert.equal(svg.children.at(-1)!.textContent, value.labels[0]!.text);
  assert.equal(svg.children.some(node => node.name === "img"), false);
  const arrows = svg.children.filter(node => node.attributes.get("class") === "roof-scene-sun-direction");
  assert.equal(arrows.length, 2);
  assert.ok(arrows.every(node => node.attributes.get("transform")?.includes("rotate(")));
  dispose(root);
});

test("web roof PV cells remain above their owning surface and back faces stay hidden", async () => {
  const { mount, dispose } = await renderer, { root, svg } = fixture(), value = scene();
  // This tile's average camera depth is behind its parent polygon's average.
  // Sorting it independently would hide it under the parent's opaque fill.
  value.faces.push({ kind: "panel", roof: 1, points: [[10, 0, 10.3], [12, 0, 10.3], [10, 2, 10.3]] });
  value.faces.push({ kind: "roof", roof: 2, points: [[0, 0, 10], [0, 10, 10], [10, 0, 10]] });
  value.faces.push({ kind: "panel", roof: 2, points: [[0, 0, 10.3], [0, 2, 10.3], [2, 0, 10.3]] });
  value.labels.push({ text: "2", kind: "roof", point: [0, 0, 12] });
  value.faces.push({ kind: "panel", roof: 3, points: [[0, 0, 0], [0, 10, 0], [0, 10, 20]] });
  mount(root, value);
  const faces = svg.children.filter(node => node.attributes.get("class")?.includes("roof-scene-face"));
  const roof = faces.findIndex(node => node.attributes.get("class")?.includes("roof-scene-roof"));
  assert.equal(faces[roof]!.attributes.get("data-roof"), "1");
  assert.ok(faces[roof + 1]!.attributes.get("class")?.includes("roof-scene-panel"));
  assert.equal(faces[roof + 1]!.attributes.get("data-roof"), "1");
  assert.equal(faces.some(node => node.attributes.get("data-roof") === "2"), false);
  assert.equal(svg.children.some(node => node.textContent === "2"), false);
  assert.equal(faces.filter(node => node.attributes.get("data-roof") === "3").length, 1);
  dispose(root);
});

test("web upright panel parents keep each exact tile above the parent across bearing and count updates", async () => {
  const { mount, update, dispose } = await renderer, value = fixture();
  const vertical = (count: number) => ({ ...scene(), faces: [{ kind: "vertical", roof: 1, points: [[-10, 0, 10], [10, 0, 10], [10, 0, 25], [-10, 0, 25]] },
    ...Array.from({ length: count }, (_, index) => ({ kind: "panel", roof: 1, points: [[index, .3, 11], [index + .8, .3, 11], [index + .8, .3, 13], [index, .3, 13]] }))] });
  const assertGrouped = (count: number) => {
    const faces = value.svg.children.filter(node => node.attributes.get("class")?.includes("roof-scene-face"));
    assert.equal(faces.length, count + 1); assert.ok(faces[0]!.attributes.get("class")?.includes("roof-scene-vertical"));
    assert.ok(faces.slice(1).every(node => node.attributes.get("class")?.includes("roof-scene-panel") && node.attributes.get("data-roof") === "1"));
  };
  mount(value.root, vertical(8)); assertGrouped(8);
  update(value.root, { ...vertical(8), frontYaw: 145 }); value.document.flush(); assertGrouped(8);
  update(value.root, vertical(7)); value.document.flush(); assertGrouped(7); assert.equal(value.camera().yaw, -35);
  update(value.root, vertical(0)); value.document.flush(); assertGrouped(0);
  dispose(value.root);
});

test("web roof edits retain the camera and coalesce redraws while independent scenes stay isolated", async () => {
  const { mount, update, dispose } = await renderer, first = fixture(), second = fixture();
  mount(first.root, scene()); mount(second.root, scene());
  first.svg.fire("keydown", { key: "+" });
  const edited = scene(); edited.frontYaw = 50; edited.faces[0]!.points.forEach(point => { point[2] = 20; });
  update(first.root, edited); update(first.root, edited);
  assert.equal(first.document.frames.size, 1);
  first.document.flush();
  assert.deepEqual(first.camera(), { yaw: 50, elevation: 32, zoom: 1.15 });
  assert.deepEqual(second.camera(), { yaw: -35, elevation: 32, zoom: 1 });
  const points = first.svg.children.find(node => node.attributes.get("class")?.includes("roof-scene-face"))!.attributes.get("points")!;
  assert.ok(points.startsWith(`180.000,${(180 - 20 * Math.cos(32 * Math.PI / 180) * 1.15).toFixed(3)}`));
  dispose(first.root); dispose(second.root);
});

function sunAngles(value: ReturnType<typeof fixture>) {
  return Object.fromEntries(["x", "y", "z"].map(axis => [axis, Number(value.svg.attributes.get(`data-sun-${axis}`))]));
}
function node(value: ReturnType<typeof fixture>, kind: string) {
  const result = value.svg.children.find(item => item.attributes.get("class") === kind);
  assert.ok(result, `Missing rendered ${kind}`); return result;
}
function coordinates(value: ReturnType<typeof fixture>, kind: string) {
  return node(value, kind).attributes.get("points")!.split(" ").map(point => point.split(",").map(Number));
}
function house(value: ReturnType<typeof fixture>) {
  return value.svg.children.filter(item => /roof-scene-(face|line|number|label-line)/.test(item.attributes.get("class") ?? ""))
    .map(item => [item.textContent, [...item.attributes]]);
}
function near(actual: number[], expected: number[], tolerance = 1e-10) {
  assert.equal(actual.length, expected.length);
  actual.forEach((value, index) => assert.ok(Math.abs(value - expected[index]!) <= tolerance, `${actual} != ${expected}`));
}
const settle = () => new Promise<void>(resolve => setImmediate(resolve));

test("web solar axes are right-handed 3D house-local rotations with fixed Rx then Ry then Rz order", async () => {
  const { rotateSolarPoint } = await renderer;
  near(rotateSolarPoint([0, 0, 1], 0, { x: 90, y: 0, z: 0 }), [0, -1, 0]);
  near(rotateSolarPoint([1, 0, 0], 0, { x: 0, y: 90, z: 0 }), [0, 1, 0]);
  near(rotateSolarPoint([1, 0, 0], 0, { x: 0, y: 0, z: 90 }), [0, 0, 1]);
  // X first sends up to local Z; Y then sends local Z to local X. Reversing the order fails this fixture.
  near(rotateSolarPoint([0, 0, 1], 0, { x: 90, y: 90, z: 0 }), [1, 0, 0]);
  for (const bearing of [-179.5, -40, 0, 42.5, 179.5]) {
    const f = bearing * Math.PI / 180, x = [Math.cos(f), -Math.sin(f), 0], z = [-Math.sin(f), -Math.cos(f), 0];
    near(rotateSolarPoint([0, 0, 1], bearing, { x: 90, y: 0, z: 0 }), z);
    near(rotateSolarPoint(x, bearing, { x: 0, y: 0, z: 90 }), [0, 0, 1]);
    const a = [3, -7, 11], b = [-2, 5, 4], angles = { x: 37.2, y: -123.5, z: 82.1 };
    const before = JSON.stringify({ a, b, angles });
    const ra = rotateSolarPoint(a, bearing, angles), rb = rotateSolarPoint(b, bearing, angles);
    assert.ok(Math.abs(Math.hypot(...ra) - Math.hypot(...a)) < 1e-10);
    assert.ok(Math.abs(ra.reduce((sum, v, i) => sum + v * rb[i]!, 0) - a.reduce((sum, v, i) => sum + v * b[i]!, 0)) < 1e-10);
    near(rotateSolarPoint([0, 0, 0], bearing, angles), [0, 0, 0]);
    assert.equal(JSON.stringify({ a, b, angles }), before);
  }
});

test("web mouse and touch gestures orbit only the camera, never sun settings or draft callbacks", async () => {
  const { mount, configureOrientation, dispose } = await renderer, value = fixture(), model = scene(), calls: number[] = [];
  const source = JSON.stringify(model);
  mount(value.root, model); configureOrientation(value.root, true, false, { invokeMethodAsync: async (_: string, delta: number) => calls.push(delta) }, 230);
  value.document.flush(); const solar = sunAngles(value), initial = value.camera();
  value.svg.fire("pointerdown", { clientX: 30, clientY: 20 });
  value.svg.fire("pointermove", { clientX: 30.5, clientY: 20.5 }); value.document.flush();
  assert.notEqual(value.camera().yaw, initial.yaw); assert.notEqual(value.camera().elevation, initial.elevation);
  const orbit = value.camera();
  value.svg.fire("pointerdown", { pointerId: 2, clientX: 130.5, clientY: 20.5 });
  value.svg.fire("pointermove", { pointerId: 2, clientX: 230.5, clientY: 20.5 }); value.document.flush();
  assert.equal(value.camera().zoom, 2); assert.equal(value.camera().yaw, orbit.yaw); assert.equal(value.camera().elevation, orbit.elevation);
  value.svg.fire("pointerup", { pointerId: 2 });
  value.svg.fire("pointermove", { clientX: 30.5, clientY: 20.5 }); value.document.flush();
  assert.deepEqual(value.camera(), { ...orbit, zoom: 2 }, "Remaining pointer must rebase without a jump");
  value.svg.fire("pointermove", { clientX: 40.5, clientY: 25.5 }); value.document.flush();
  assert.notEqual(value.camera().yaw, orbit.yaw); assert.notEqual(value.camera().elevation, orbit.elevation);
  await settle(); assert.deepEqual(calls, []); assert.deepEqual(sunAngles(value), solar); assert.equal(JSON.stringify(model), source);
  value.svg.fire("pointercancel"); const before = value.document.requests;
  value.svg.fire("pointermove", { clientX: 300, clientY: 300 });
  assert.equal(value.document.requests, before); assert.equal(value.svg.captured.size, 0);
  dispose(value.root);
});

test("web arrow keys orbit the camera in editing mode and zoom/reset never modify solar angles", async () => {
  const { mount, configureOrientation, dispose } = await renderer, value = fixture(), calls: number[] = [];
  mount(value.root, scene()); configureOrientation(value.root, true, false, { invokeMethodAsync: async (_: string, delta: number) => calls.push(delta) }, 230); value.document.flush();
  value.root.fire("click", { target: value.button("sun-x-plus") }); value.document.flush();
  assert.deepEqual(sunAngles(value), { x: 10, y: 0, z: 0 });
  assert.equal(value.svg.fire("keydown", { key: "ArrowUp" }).prevented, true);
  assert.equal(value.svg.fire("keydown", { key: "ArrowRight" }).prevented, true); value.document.flush();
  assert.deepEqual(value.camera(), { yaw: -25, elevation: 42, zoom: 1 });
  assert.equal(value.svg.fire("keydown", { key: "Tab" }).prevented, false);
  assert.equal(value.svg.fire("keydown", { key: "+", altKey: true }).prevented, false);
  value.svg.fire("wheel", { deltaY: -10000, deltaMode: 0 }); value.document.flush(); assert.equal(value.camera().zoom, 2.5);
  for (let i = 0; i < 30; ++i) value.svg.fire("keydown", { key: "-" });
  value.document.flush(); assert.equal(value.camera().zoom, .7);
  const reset = value.button("reset"); reset.disabled = true;
  value.root.fire("click", { target: reset }); value.document.flush(); assert.equal(value.camera().zoom, .7);
  reset.disabled = false; value.root.fire("click", { target: reset }); value.document.flush();
  assert.deepEqual(value.camera(), { yaw: -35, elevation: 32, zoom: 1 }); assert.deepEqual(sunAngles(value), { x: 10, y: 0, z: 0 });
  await settle(); assert.deepEqual(calls, []); assert.equal(value.root.listeners.has("wheel"), false); dispose(value.root);
});

test("web roof disposal removes captures, all listeners and queued frames before showing the static fallback", async () => {
  const { mount, update, dispose } = await renderer, value = fixture(); mount(value.root, scene());
  value.svg.fire("pointerdown", { pointerId: 9 }); update(value.root, scene());
  assert.equal(value.document.frames.size, 1); assert.equal(value.svg.style.touchAction, "none");
  dispose(value.root); dispose(value.root);
  assert.equal(value.document.frames.size, 0); assert.equal(value.svg.captured.size, 0);
  assert.equal(value.svg.style.touchAction, "pan-y"); assert.equal(value.svg.children.length, 0);
  assert.equal(value.root.classes.has("scene-ready"), false);
  assert.equal([...value.svg.listeners.values(), ...value.root.listeners.values()].some(listeners => listeners.size), false);
  value.svg.fire("pointermove", { pointerId: 9, clientX: 100 }); value.document.flush();
  assert.equal(value.document.requests, 1);
  mount(value.root, scene()); assert.deepEqual(value.camera(), { yaw: -35, elevation: 32, zoom: 1 }); dispose(value.root);
});

test("web each explicit solar axis rotates the entire sky in 3D while house and physical source remain fixed", async () => {
  const { mount, dispose } = await renderer;
  // At frontYaw0 local X is east, local Y is up and local Z is south/away.
  const transformed = (point: number[], axis: string) => {
    const [x, y, z] = point as [number, number, number];
    return axis === "x" ? [x, -z, y] : axis === "y" ? [-y, x, z] : [-z, y, x];
  };
  const project = ([x, y, z]: number[]) => [180 + x!, 180 + y! * Math.sin(32 * Math.PI / 180) - z! * Math.cos(32 * Math.PI / 180)];
  for (const axis of ["x", "y", "z"]) {
    const value = fixture(), model = scene(); model.frontYaw = 0;
    model.labels.push({ text: "1", kind: "roof", point: [0, 0, 12] });
    const source = JSON.stringify(model); mount(value.root, model); const fixed = house(value), camera = value.camera();
    for (let i = 0; i < 9; ++i) value.root.fire("click", { target: value.button(`sun-${axis}-plus`) }); value.document.flush();
    assert.equal(sunAngles(value)[axis], 90); assert.deepEqual(house(value), fixed); assert.deepEqual(value.camera(), camera);
    for (const [kind, points] of [["roof-scene-ground", model.ground], ["roof-scene-axis", model.axes[0]!], ["roof-scene-sun-path", model.sunPaths[0]!]] as const) {
      const actual = coordinates(value, kind); points.forEach((point, i) => near(actual[i]!, project(transformed(point, axis)), .002));
    }
    const first = project(transformed(model.crossings[0]!, axis));
    const crossing = node(value, "roof-scene-crossing"), cardinal = node(value, "roof-scene-label roof-scene-cardinal");
    near([Number(crossing.attributes.get("cx")), Number(crossing.attributes.get("cy"))], first, .001);
    near([Number(cardinal.attributes.get("x")), Number(cardinal.attributes.get("y"))], first, .001);
    const sun = node(value, "roof-scene-sun-current").attributes.get("transform")!.match(/translate\(([^ )]+) ([^ )]+)\)/)!;
    near([Number(sun[1]), Number(sun[2])], project(transformed(model.sunNow, axis)), .001);
    const arrows = value.svg.children.filter(item => item.attributes.get("class") === "roof-scene-sun-direction"); assert.equal(arrows.length, 2);
    for (const [i, arrow] of arrows.entries()) {
      const index = Math.floor(model.sunPaths[0]!.length * [1 / 3, 2 / 3][i]!);
      const at = project(transformed(model.sunPaths[0]![index]!, axis)), next = project(transformed(model.sunPaths[0]![index + 1]!, axis));
      const transform = arrow.attributes.get("transform")!.match(/translate\(([^ )]+) ([^ )]+)\) rotate\(([^ )]+)\)/)!;
      near([Number(transform[1]), Number(transform[2])], at, .001);
      assert.ok(Math.abs(Number(transform[3]) - Math.atan2(next[1]! - at[1]!, next[0]! - at[0]!) * 180 / Math.PI) < .001);
    }
    for (let i = 0; i < 9; ++i) value.root.fire("click", { target: value.button(`sun-${axis}-minus`) }); value.document.flush();
    assert.deepEqual(sunAngles(value), { x: 0, y: 0, z: 0 }); assert.equal(JSON.stringify(model), source); dispose(value.root);
  }
});

test("web only explicit Y buttons serialize physical direction callbacks and discard pending work on guards or disposal", async () => {
  const { mount, configureOrientation, updateOrientation, dispose } = await renderer, value = fixture(), deltas: number[] = [];
  let release: (() => void) | undefined;
  const bridge = { invokeMethodAsync: (method: string, delta: number) => { assert.equal(method, "RotateHouse"); deltas.push(delta); return new Promise<void>(resolve => { release = resolve; }); } };
  mount(value.root, scene()); configureOrientation(value.root, true, false, bridge, 230); value.document.flush();
  value.root.fire("click", { target: value.button("sun-y-plus") }); await settle();
  value.root.fire("click", { target: value.button("sun-y-plus") }); value.root.fire("click", { target: value.button("sun-y-minus") });
  value.root.fire("click", { target: value.button("sun-y-plus") });
  value.root.fire("click", { target: value.button("sun-x-plus") }); value.root.fire("click", { target: value.button("sun-z-minus") });
  value.svg.fire("pointerdown"); value.svg.fire("pointermove", { clientX: 10, clientY: 20 }); value.svg.fire("keydown", { key: "ArrowRight" }); value.document.flush();
  assert.deepEqual(deltas, [10]); assert.deepEqual(sunAngles(value), { x: 10, y: 0, z: -10 });
  release!(); await settle(); assert.deepEqual(deltas, [10, 10]);
  configureOrientation(value.root, false, false, bridge, 250); value.document.flush();
  release!(); await settle(); assert.deepEqual(deltas, [10, 10]);
  // Read-only Y is a local solar view angle, never an installation edit.
  value.root.fire("click", { target: value.button("sun-y-plus") }); value.document.flush();
  assert.equal(sunAngles(value).y, 10); assert.deepEqual(deltas, [10, 10]);
  configureOrientation(value.root, true, true, bridge, 250); value.document.flush(); const guarded = sunAngles(value);
  for (const axis of ["x", "y", "z"]) value.root.fire("click", { target: value.button(`sun-${axis}-plus`) }); value.document.flush();
  assert.deepEqual(sunAngles(value), guarded); assert.deepEqual(deltas, [10, 10]);
  configureOrientation(value.root, true, false, bridge, 250); value.root.fire("click", { target: value.button("sun-y-minus") }); await settle();
  assert.deepEqual(deltas, [10, 10, -10]);
  value.root.fire("click", { target: value.button("sun-y-plus") }); dispose(value.root); release!(); await settle();
  assert.deepEqual(deltas, [10, 10, -10]); assert.equal(value.svg.children.length, 0);
});

test("web scene updates preserve independent camera offsets and solar view angles, reset leaves the draft unchanged", async () => {
  const { mount, configureOrientation, updateOrientation, dispose } = await renderer, value = fixture(), model = scene();
  const calls: number[] = [], bridge = { invokeMethodAsync: async (_: string, delta: number) => calls.push(delta) };
  mount(value.root, model); configureOrientation(value.root, true, false, bridge, 230); value.document.flush();
  value.svg.fire("keydown", { key: "ArrowRight" }); value.svg.fire("keydown", { key: "ArrowUp" }); value.svg.fire("keydown", { key: "+" });
  value.root.fire("click", { target: value.button("sun-x-plus") }); value.root.fire("click", { target: value.button("sun-z-plus") }); value.document.flush();
  value.root.fire("click", { target: value.button("sun-y-plus") }); await settle(); assert.deepEqual(calls, [10]);
  const changed = { ...model, frontYaw: -25 };
  updateOrientation(value.root, changed, true, false, bridge, 240); value.document.flush();
  assert.deepEqual(value.camera(), { yaw: -15, elevation: 42, zoom: 1.15 }); assert.deepEqual(sunAngles(value), { x: 10, y: 0, z: 10 });
  value.svg.fire("keydown", { key: "Home" }); value.document.flush();
  assert.deepEqual(value.camera(), { yaw: -25, elevation: 32, zoom: 1 }); assert.deepEqual(sunAngles(value), { x: 10, y: 0, z: 10 });
  assert.equal(changed.frontYaw, -25); assert.deepEqual(calls, [10]); dispose(value.root);
});

test("web leaving direction mode cancels a not-yet-started Y edit even if a new editing bridge is installed", async () => {
  const { mount, configureOrientation, dispose } = await renderer, value = fixture(), oldCalls: number[] = [], newCalls: number[] = [];
  const oldBridge = { invokeMethodAsync: async (_: string, delta: number) => oldCalls.push(delta) };
  const newBridge = { invokeMethodAsync: async (_: string, delta: number) => newCalls.push(delta) };
  mount(value.root, scene()); configureOrientation(value.root, true, false, oldBridge, 230);
  value.root.fire("click", { target: value.button("sun-y-plus") });
  configureOrientation(value.root, false, false, oldBridge, 230);
  configureOrientation(value.root, true, false, newBridge, 230);
  await settle(); assert.deepEqual(oldCalls, []); assert.deepEqual(newCalls, [], "A cancelled draft edit cannot target a new bridge");
  value.root.fire("click", { target: value.button("sun-y-minus") }); await settle();
  assert.deepEqual(newCalls, [-10]); dispose(value.root);
});

test("web an obsolete bridge rejection cannot erase queued Y edits from a resumed direction scope", async () => {
  const { mount, configureOrientation, dispose } = await renderer, value = fixture(), newCalls: number[] = [];
  let rejectOld: ((error: Error) => void) | undefined;
  const oldBridge = { invokeMethodAsync: () => new Promise<void>((_resolve, reject) => { rejectOld = reject; }) };
  const newBridge = { invokeMethodAsync: async (_: string, delta: number) => newCalls.push(delta) };
  mount(value.root, scene()); configureOrientation(value.root, true, false, oldBridge, 230);
  value.root.fire("click", { target: value.button("sun-y-plus") }); await settle();
  configureOrientation(value.root, false, false, oldBridge, 230);
  configureOrientation(value.root, true, false, newBridge, 230);
  value.root.fire("click", { target: value.button("sun-y-minus") });
  rejectOld!(new Error("Old circuit ended")); await settle();
  assert.deepEqual(newCalls, [-10]); dispose(value.root);
});
