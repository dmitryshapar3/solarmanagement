// Dragging orbits only the camera. Explicit buttons rotate the solar frame in
// house-local 3D axes; only the Y buttons in direction mode edit roof bearings.
const states = new WeakMap();
const ns = "http://www.w3.org/2000/svg";
const normalizeYaw = value => ((value + 180) % 360 + 360) % 360 - 180;
const frontYaw = scene => normalizeYaw(Number.isFinite(scene?.frontYaw) ? scene.frontYaw : -35);
const initialCamera = scene => ({ yaw: frontYaw(scene), elevation: 32, zoom: 1 });
const clamp = (value, min, max) => Math.max(min, Math.min(max, value));
const finitePoint = point => Array.isArray(point) && point.length === 3 && point.every(Number.isFinite);
const radians = degrees => degrees * Math.PI / 180;

function project(point, camera) {
    const yaw = radians(camera.yaw), elevation = radians(camera.elevation);
    const horizontal = point[0] * Math.cos(yaw) - point[1] * Math.sin(yaw);
    const depth = point[0] * Math.sin(yaw) + point[1] * Math.cos(yaw);
    return { x: 180 + horizontal * camera.zoom, y: 180 + (depth * Math.sin(elevation) - point[2] * Math.cos(elevation)) * camera.zoom,
        depth: depth * Math.cos(elevation) + point[2] * Math.sin(elevation) };
}

// Right-handed house axes: X across the gable, Y up, Z away from the front.
// Apply X, then Y, then Z before the independent camera projection. The input
// ENU solar positions remain immutable, including the location-derived altitude.
export function rotateSolarPoint(point, houseYaw, angles) {
    const f = radians(houseYaw), cx = Math.cos(radians(angles.x)), sx = Math.sin(radians(angles.x));
    const cy = Math.cos(radians(angles.y)), sy = Math.sin(radians(angles.y));
    const cz = Math.cos(radians(angles.z)), sz = Math.sin(radians(angles.z));
    const x = point[0] * Math.cos(f) - point[1] * Math.sin(f), y = point[2];
    const z = -point[0] * Math.sin(f) - point[1] * Math.cos(f);
    const y1 = y * cx - z * sx, z1 = y * sx + z * cx;
    const x2 = x * cy + z1 * sy, z2 = -x * sy + z1 * cy;
    const x3 = x2 * cz - y1 * sz, y3 = x2 * sz + y1 * cz;
    return [x3 * Math.cos(f) - z2 * Math.sin(f), -x3 * Math.sin(f) - z2 * Math.cos(f), y3];
}

function element(document, name, className, attributes = {}) {
    const node = document.createElementNS(ns, name);
    node.setAttribute("class", className);
    for (const [key, value] of Object.entries(attributes)) node.setAttribute(key, String(value));
    return node;
}

function draw(state) {
    const { svg, root, scene, camera } = state;
    if (!scene || !Array.isArray(scene.faces)) return;
    const document = svg.ownerDocument, content = document.createDocumentFragment();
    const skyPoint = point => project(rotateSolarPoint(point, frontYaw(scene), state.sunAngles), camera);
    const projected = points => points.map(point => project(point, camera));
    const coordinates = points => projected(points).map(point => `${point.x.toFixed(3)},${point.y.toFixed(3)}`).join(" ");
    const skyCoordinates = points => points.map(skyPoint).map(point => `${point.x.toFixed(3)},${point.y.toFixed(3)}`).join(" ");
    const line = (points, className, sky = false) => {
        if (Array.isArray(points) && points.length > 1 && points.every(finitePoint))
            content.appendChild(element(document, "polyline", className, { points: sky ? skyCoordinates(points) : coordinates(points), fill: "none" }));
    };
    if (Array.isArray(scene.ground) && scene.ground.length > 2 && scene.ground.every(finitePoint))
        content.appendChild(element(document, "polygon", "roof-scene-ground", { points: skyCoordinates(scene.ground) }));
    for (const axis of scene.axes ?? []) line(axis, "roof-scene-axis", true);
    for (const path of scene.sunPaths ?? []) {
        line(path, "roof-scene-sun-path", true);
        if (Array.isArray(path) && path.length > 4 && path.every(finitePoint)) for (const fraction of [1 / 3, 2 / 3]) {
            const index = Math.min(path.length - 2, Math.floor(path.length * fraction));
            const point = skyPoint(path[index]), next = skyPoint(path[index + 1]);
            const angle = Math.atan2(next.y - point.y, next.x - point.x) * 180 / Math.PI;
            content.appendChild(element(document, "path", "roof-scene-sun-direction", { d: "M-5 -4L0 0L-5 4", transform: `translate(${point.x} ${point.y}) rotate(${angle})`, fill: "none" }));
        }
    }
    // Positive camera depth is nearer the viewer. Coplanar PV tiles stay with
    // their roof so a parent polygon cannot paint over its own far-side cells.
    const validFaces = scene.faces.filter(face => ["wall", "roof", "vertical", "panel"].includes(face.kind) && Array.isArray(face.points) && face.points.length > 2 && face.points.every(finitePoint));
    const roofs = new Set(validFaces.filter(face => ["roof", "vertical"].includes(face.kind) && face.roof > 0).map(face => face.roof));
    const cameraVector = [Math.sin(radians(camera.yaw)) * Math.cos(radians(camera.elevation)), Math.cos(radians(camera.yaw)) * Math.cos(radians(camera.elevation)), Math.sin(radians(camera.elevation))];
    const visible = face => {
        if (face.kind === "panel" || face.kind === "vertical") return true; // Upright arrays can be inspected from either side.
        const a = face.points[0];
        for (let i = 1; i < face.points.length - 1; ++i) {
            const b = face.points[i].map((value, axis) => value - a[axis]), c = face.points[i + 1].map((value, axis) => value - a[axis]);
            const normal = [b[1] * c[2] - b[2] * c[1], b[2] * c[0] - b[0] * c[2], b[0] * c[1] - b[1] * c[0]];
            const length = Math.hypot(...normal);
            if (length > 0) return normal.reduce((sum, value, axis) => sum + value * cameraVector[axis], 0) / length > 1e-10;
        }
        return false;
    };
    const groups = validFaces.filter(face => !(face.kind === "panel" && roofs.has(face.roof)) && visible(face))
        .map(face => ({ ...face, depth: projected(face.points).reduce((sum, point) => sum + point.depth, 0) / face.points.length }))
        .sort((left, right) => left.depth - right.depth);
    const addFace = face => content.appendChild(element(document, "polygon", `roof-scene-face roof-scene-${face.kind}`, { points: coordinates(face.points), "data-roof": face.roof }));
    for (const face of groups) {
        addFace(face);
        if (face.kind === "roof" || face.kind === "vertical") for (const tile of validFaces.filter(item => item.kind === "panel" && item.roof === face.roof)) addFace(tile);
    }
    for (const sceneLine of scene.lines ?? []) {
        if (["ridge", "grid", "bearing"].includes(sceneLine.kind)) line(sceneLine.points, `roof-scene-line roof-scene-${sceneLine.kind}`);
    }
    for (const point of scene.crossings ?? []) {
        if (!finitePoint(point)) continue;
        const location = skyPoint(point);
        content.appendChild(element(document, "circle", "roof-scene-crossing", { cx: location.x, cy: location.y, r: 3 }));
    }
    if (finitePoint(scene.sunNow)) {
        const point = skyPoint(scene.sunNow);
        const sun = element(document, "g", "roof-scene-sun-current", { transform: `translate(${point.x} ${point.y})` });
        sun.appendChild(element(document, "circle", "", { r: 8 }));
        sun.appendChild(element(document, "path", "", { d: "M0 -14V-11M0 11V14M-14 0H-11M11 0H14M-10 -10L-8 -8M8 8L10 10M-10 10L-8 8M8 -8L10 -10", fill: "none" }));
        content.appendChild(sun);
    }
    const visibleRoofNumbers = new Set(groups.filter(face => ["roof", "vertical", "panel"].includes(face.kind)).map(face => face.roof));
    for (const label of scene.labels ?? []) {
        if (!finitePoint(label.point) || !["roof", "cardinal"].includes(label.kind)) continue;
        if (label.kind === "roof" && Number.isFinite(Number(label.text)) && !visibleRoofNumbers.has(Number(label.text))) continue;
        let point = label.kind === "cardinal" ? skyPoint(label.point) : project(label.point, camera);
        if (label.kind === "roof") {
            const face = groups.find(face => ["roof", "vertical"].includes(face.kind) && face.roof === Number(label.text));
            if (face) {
                const polygon = projected(face.points), corner = polygon.reduce((lowest, p) => p.y > lowest.y ? p : lowest);
                const centerX = polygon.reduce((sum, p) => sum + p.x, 0) / polygon.length;
                point = { x: corner.x + Math.sign(corner.x - centerX) * 6, y: corner.y + 14 };
                content.appendChild(element(document, "polyline", "roof-scene-label-line", { points: `${corner.x},${corner.y} ${point.x},${point.y - 5}`, fill: "none" }));
            }
        }
        const text = element(document, "text", `roof-scene-label ${label.kind === "roof" ? "roof-scene-number" : "roof-scene-cardinal"}`, { x: point.x, y: point.y, "text-anchor": "middle", "dominant-baseline": "middle" });
        text.textContent = String(label.text ?? "");
        content.appendChild(text);
    }
    svg.replaceChildren(content);
    svg.setAttribute("data-yaw", String(camera.yaw));
    svg.setAttribute("data-elevation", String(camera.elevation));
    svg.setAttribute("data-zoom", String(camera.zoom));
    for (const axis of ["x", "y", "z"]) svg.setAttribute(`data-sun-${axis}`, String(state.sunAngles[axis]));
    svg.setAttribute("data-direction-editing", String(Boolean(state.orientation?.enabled && !state.orientation.disabled)));
    root.classList.add("scene-ready");
}

function schedule(state) {
    if (state.frame !== null) return;
    state.frame = state.window.requestAnimationFrame(() => { state.frame = null; if (!state.disposed) draw(state); });
}

function setCamera(state, camera) {
    state.camera = { yaw: normalizeYaw(camera.yaw), elevation: clamp(camera.elevation, -80, 80), zoom: clamp(camera.zoom, .7, 2.5) };
    schedule(state);
}

function rotateSun(state, axis, delta) {
    if (state.disposed || state.orientation?.disabled || !Number.isFinite(delta)) return;
    if (axis === "y" && state.orientation?.enabled) rotateDirection(state, delta);
    else {
        state.sunAngles[axis] = normalizeYaw(state.sunAngles[axis] + delta);
        schedule(state);
    }
}

function rebase(state) {
    const points = [...state.pointers.values()];
    state.gesture = { camera: { ...state.camera }, first: points[0], distance: points.length > 1 ? Math.max(1, Math.hypot(points[1].x - points[0].x, points[1].y - points[0].y)) : null };
}

// Serialize explicit Y-button edits while the previous draft update is in
// flight. Pointer and keyboard camera controls never enter this queue.
function rotateDirection(state, delta) {
    if (state.disposed || !state.orientation?.enabled || state.orientation.disabled || !Number.isFinite(delta)) return;
    state.directionPending += delta;
    const pump = () => {
        if (state.disposed || state.directionInFlight || !state.orientation?.enabled || state.orientation.disabled) return;
        const amount = clamp(Math.trunc(state.directionPending), -360, 360);
        if (!amount) return;
        state.directionPending -= amount;
        const epoch = state.directionEpoch, bridge = state.orientation.bridge;
        state.directionInFlight = true;
        Promise.resolve().then(() => {
            if (state.disposed || state.directionEpoch !== epoch || !state.orientation?.enabled || state.orientation.disabled) return;
            return bridge.invokeMethodAsync("RotateHouse", amount);
        })
            .catch(() => { if (state.directionEpoch === epoch) state.directionPending = 0; })
            .finally(() => { state.directionInFlight = false; pump(); });
    };
    pump();
}

function action(state, name) {
    const axisAction = /^sun-([xyz])-(minus|plus)$/.exec(name);
    if (axisAction) { rotateSun(state, axisAction[1], axisAction[2] === "plus" ? 10 : -10); return true; }
    const camera = { ...state.camera };
    switch (name) {
        case "zoom-in": camera.zoom *= 1.15; break;
        case "zoom-out": camera.zoom /= 1.15; break;
        case "view-left": camera.yaw -= 10; break;
        case "view-right": camera.yaw += 10; break;
        case "view-up": camera.elevation += 10; break;
        case "view-down": camera.elevation -= 10; break;
        case "reset": setCamera(state, initialCamera(state.scene)); rebase(state); return true;
        default: return false;
    }
    setCamera(state, camera); rebase(state); return true;
}

export function mount(root, scene) {
    if (states.has(root)) { update(root, scene); return; }
    const svg = root.querySelector("svg.roof-scene-interactive");
    if (!svg) return;
    const state = { root, svg, scene, window: svg.ownerDocument.defaultView, camera: initialCamera(scene), sunAngles: { x: 0, y: 0, z: 0 }, frame: null,
        pointers: new Map(), gesture: null, listeners: [], disposed: false, previousTouchAction: svg.style.touchAction,
        orientation: null, directionEpoch: 0, directionPending: 0, directionInFlight: false };
    const listen = (target, name, handler, options) => { target.addEventListener(name, handler, options); state.listeners.push(() => target.removeEventListener(name, handler, options)); };
    const end = event => {
        if (!state.pointers.delete(event.pointerId)) return;
        if (svg.hasPointerCapture?.(event.pointerId)) svg.releasePointerCapture(event.pointerId);
        rebase(state);
    };
    listen(svg, "pointerdown", event => {
        if (event.button !== 0) return;
        event.preventDefault();
        state.pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
        try { svg.setPointerCapture(event.pointerId); } catch { /* A pointer cancelled before capture needs no global listener. */ }
        rebase(state);
    });
    listen(svg, "pointermove", event => {
        if (!state.pointers.has(event.pointerId)) return;
        event.preventDefault();
        state.pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
        const points = [...state.pointers.values()], gesture = state.gesture;
        if (!gesture?.first) return;
        if (points.length > 1 && gesture.distance !== null) {
            const distance = Math.hypot(points[1].x - points[0].x, points[1].y - points[0].y);
            setCamera(state, { ...state.camera, zoom: gesture.camera.zoom * distance / gesture.distance });
        } else {
            setCamera(state, { ...gesture.camera,
                yaw: gesture.camera.yaw + (points[0].x - gesture.first.x) * .6,
                elevation: gesture.camera.elevation - (points[0].y - gesture.first.y) * .6 });
        }
    });
    for (const name of ["pointerup", "pointercancel", "lostpointercapture"]) listen(svg, name, end);
    listen(svg, "wheel", event => {
        event.preventDefault();
        const pixels = event.deltaY * (event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? 320 : 1);
        setCamera(state, { ...state.camera, zoom: state.camera.zoom * Math.exp(clamp(-pixels * .002, -1, 1)) });
        rebase(state);
    }, { passive: false });
    listen(svg, "keydown", event => {
        if (event.altKey || event.ctrlKey || event.metaKey) return;
        const name = { ArrowLeft: "view-left", ArrowRight: "view-right", ArrowUp: "view-up", ArrowDown: "view-down", "+": "zoom-in", "=": "zoom-in", "-": "zoom-out", "_": "zoom-out", Home: "reset" }[event.key];
        if (name && action(state, name)) { event.preventDefault(); event.stopPropagation(); }
    });
    listen(root, "click", event => {
        const button = event.target.closest?.("[data-roof-action]");
        if (button && root.contains(button) && !button.disabled && action(state, button.dataset.roofAction)) event.preventDefault();
    });
    svg.style.touchAction = "none";
    states.set(root, state);
    draw(state);
}

export function update(root, scene) {
    const state = states.get(root);
    if (!state) { mount(root, scene); return; }
    state.camera.yaw = normalizeYaw(state.camera.yaw + normalizeYaw(frontYaw(scene) - frontYaw(state.scene)));
    state.scene = scene;
    rebase(state);
    schedule(state);
}

export function updateOrientation(root, scene, enabled, disabled, bridge, bearing) {
    const state = states.get(root);
    if (!state || state.disposed) return;
    state.camera.yaw = normalizeYaw(state.camera.yaw + normalizeYaw(frontYaw(scene) - frontYaw(state.scene)));
    state.scene = scene;
    configureOrientation(root, enabled, disabled, bridge, bearing);
}

export function configureOrientation(root, enabled, disabled, bridge, bearing) {
    const state = states.get(root);
    if (!state || state.disposed) return;
    const next = { enabled: Boolean(enabled), disabled: Boolean(disabled), bridge, bearing };
    if (!state.orientation || state.orientation.enabled !== next.enabled || state.orientation.disabled !== next.disabled || state.orientation.bridge !== bridge) {
        state.directionEpoch++;
        state.directionPending = 0;
    }
    state.orientation = next;
    if (!enabled || disabled) state.directionPending = 0;
    rebase(state);
    schedule(state);
}

export function dispose(root) {
    const state = states.get(root);
    if (!state) return;
    state.disposed = true;
    state.directionPending = 0;
    if (state.frame !== null) state.window.cancelAnimationFrame(state.frame);
    for (const remove of state.listeners) remove();
    for (const id of state.pointers.keys()) if (state.svg.hasPointerCapture?.(id)) state.svg.releasePointerCapture(id);
    state.pointers.clear();
    state.svg.style.touchAction = state.previousTouchAction;
    state.svg.replaceChildren();
    root.classList.remove("scene-ready");
    states.delete(root);
}
