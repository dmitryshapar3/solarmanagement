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
  querySelector(selector: string): DomNode | null { return this.children.find(child => selector === "svg.roof-scene-interactive" && child.name === "svg") ?? null; }
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
type Renderer = { mount(root: any, scene: any): void; update(root: any, scene: any): void; dispose(root: any): void };
const renderer: Promise<Renderer> = readFile(new URL("../../src/DeyeSolar.Web/wwwroot/js/roof-scene.js", import.meta.url), "utf8")
  .then(source => import(`data:text/javascript;base64,${Buffer.from(source).toString("base64")}`));
const scene = () => ({ faces: [{ kind: "roof", roof: 1, points: [[0, 0, 10], [10, 0, 10], [0, 10, 10]] }],
  ground: [[-20, -20, 0], [20, -20, 0], [20, 20, 0]], axes: [[[0, 0, 0], [10, 0, 0]]],
  lines: [{ kind: "ridge", points: [[0, 0, 10], [10, 0, 10]] }], sunPaths: [[[0, 100, 0], [0, 80, 30], [0, 60, 60], [0, 30, 80], [0, 0, 100], [0, -30, 80]]],
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

test("web roof edits retain the camera and coalesce redraws while independent scenes stay isolated", async () => {
  const { mount, update, dispose } = await renderer, first = fixture(), second = fixture();
  mount(first.root, scene()); mount(second.root, scene());
  first.svg.fire("keydown", { key: "ArrowRight" });
  first.svg.fire("keydown", { key: "+" });
  const edited = scene(); edited.faces[0]!.points[0]![2] = 20;
  update(first.root, edited); update(first.root, edited);
  assert.equal(first.document.frames.size, 1);
  first.document.flush();
  assert.deepEqual(first.camera(), { yaw: -25, elevation: 32, zoom: 1.15 });
  assert.deepEqual(second.camera(), { yaw: -35, elevation: 32, zoom: 1 });
  const points = first.svg.children.find(node => node.attributes.get("class")?.includes("roof-scene-face"))!.attributes.get("points")!;
  assert.ok(points.startsWith(`180.000,${(180 - 20 * Math.cos(32 * Math.PI / 180) * 1.15).toFixed(3)}`));
  dispose(first.root); dispose(second.root);
});

test("web roof drag and pinch rebase when touches enter, leave or cancel without orbit jumps", async () => {
  const { mount, dispose } = await renderer, value = fixture(); mount(value.root, scene());
  value.svg.fire("pointerdown");
  value.svg.fire("pointermove", { clientX: 20, clientY: 10 }); value.document.flush();
  assert.deepEqual(value.camera(), { yaw: -26, elevation: 28.5, zoom: 1 });
  value.svg.fire("pointerdown", { pointerId: 2, clientX: 100, clientY: 10 });
  value.svg.fire("pointermove", { pointerId: 2, clientX: 180, clientY: 10 }); value.document.flush();
  assert.deepEqual(value.camera(), { yaw: -26, elevation: 28.5, zoom: 2 });
  value.svg.fire("pointerup", { pointerId: 2 });
  value.svg.fire("pointermove", { clientX: 30, clientY: 10 }); value.document.flush();
  assert.deepEqual(value.camera(), { yaw: -21.5, elevation: 28.5, zoom: 2 });
  value.svg.fire("pointercancel");
  const before = value.document.requests;
  value.svg.fire("pointermove", { clientX: 300, clientY: 300 });
  assert.equal(value.document.requests, before);
  assert.equal(value.svg.captured.size, 0);
  dispose(value.root);
});

test("web roof keyboard, local wheel and visible buttons clamp zoom/elevation and reset accessibly", async () => {
  const { mount, dispose } = await renderer, value = fixture(); mount(value.root, scene());
  assert.equal(value.svg.fire("keydown", { key: "ArrowUp" }).prevented, true);
  assert.equal(value.svg.fire("keydown", { key: "Tab" }).prevented, false);
  assert.equal(value.svg.fire("keydown", { key: "ArrowUp", altKey: true }).prevented, false);
  for (let i = 0; i < 30; ++i) value.root.fire("click", { target: value.button("up") });
  value.svg.fire("wheel", { deltaY: -10000, deltaMode: 0 }); value.document.flush();
  assert.equal(value.camera().elevation, 85); assert.equal(value.camera().zoom, 2.5);
  for (let i = 0; i < 30; ++i) { value.svg.fire("keydown", { key: "ArrowDown" }); value.svg.fire("keydown", { key: "-" }); }
  value.document.flush(); assert.equal(value.camera().elevation, 10); assert.equal(value.camera().zoom, .7);
  const reset = value.button("reset"); reset.disabled = true;
  value.root.fire("click", { target: reset }); value.document.flush(); assert.equal(value.camera().zoom, .7);
  reset.disabled = false; value.root.fire("click", { target: reset }); value.document.flush();
  assert.deepEqual(value.camera(), { yaw: -35, elevation: 32, zoom: 1 });
  assert.equal(value.root.listeners.has("wheel"), false);
  dispose(value.root);
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
