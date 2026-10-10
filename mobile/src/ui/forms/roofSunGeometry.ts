/** Approximate NOAA geometric solar position, clockwise from true north.
 * https://gml.noaa.gov/grad/solcalc/solareqns.PDF
 * Diagram only; no refraction, shadows, irradiance or energy prediction.
 * Horizon crossings use the solar centre at 0°, not apparent sunrise.
 */
export type SunPoint = { x: number; y: number; azimuth: number; elevation: number; at: number };
export type SunDay = { date: string; timeZoneId: string; startsAt: number; endsAt: number; paths: SunPoint[][]; now: SunPoint | null; sunrise: number | null; sunset: number | null; state: "normal" | "polar-day" | "polar-night" };
export type DiagramRoof = { number: number; capacity: number; tilt: number; azimuth: number; width: number; depth: number; centerX: number; panelCount?: number | null; panelsPerRow?: number | null };
export type RoofSunSite = { latitude: number; longitude: number; timeZoneId: string; roof1Kwp: number; roof1Tilt: number; roof1Azimuth: number; roof2Kwp: number; roof2Tilt: number; roof2Azimuth: number; roof1PanelCount?: number | null; roof2PanelCount?: number | null; roof1PanelsPerRow?: number | null; roof2PanelsPerRow?: number | null };
const rad = Math.PI / 180, center = 160, radius = 112;
const leap = (year: number) => year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
export function sunPosition(latitude: number, longitude: number, at: number): SunPoint {
  const utc = new Date(at), year = utc.getUTCFullYear();
  const minutes = utc.getUTCHours() * 60 + utc.getUTCMinutes() + utc.getUTCSeconds() / 60 + utc.getUTCMilliseconds() / 60000;
  const day = Math.floor((Date.UTC(year, utc.getUTCMonth(), utc.getUTCDate()) - Date.UTC(year, 0, 1)) / 86400000) + 1;
  const gamma = 2 * Math.PI / (leap(year) ? 366 : 365) * (day - 1 + (minutes / 60 - 12) / 24);
  const equation = 229.18 * (.000075 + .001868 * Math.cos(gamma) - .032077 * Math.sin(gamma) - .014615 * Math.cos(2 * gamma) - .040849 * Math.sin(2 * gamma));
  const declination = .006918 - .399912 * Math.cos(gamma) + .070257 * Math.sin(gamma) - .006758 * Math.cos(2 * gamma) + .000907 * Math.sin(2 * gamma) - .002697 * Math.cos(3 * gamma) + .00148 * Math.sin(3 * gamma);
  const hourAngle = ((((minutes + equation + 4 * longitude) % 1440 + 1440) % 1440) / 4 - 180) * rad, lat = latitude * rad;
  const elevation = Math.asin(Math.max(-1, Math.min(1, Math.sin(lat) * Math.sin(declination) + Math.cos(lat) * Math.cos(declination) * Math.cos(hourAngle)))) / rad;
  const azimuth = (Math.atan2(-Math.cos(declination) * Math.sin(hourAngle), Math.cos(lat) * Math.sin(declination) - Math.sin(lat) * Math.cos(declination) * Math.cos(hourAngle)) / rad + 360) % 360;
  const distance = radius * (90 - Math.max(0, Math.min(90, elevation))) / 90;
  return { x: center + Math.sin(azimuth * rad) * distance, y: center - Math.cos(azimuth * rad) * distance, azimuth, elevation, at };
}

function localFields(format: Intl.DateTimeFormat, at: number) {
  const parts = Object.fromEntries(format.formatToParts(at).map(part => [part.type, part.value]));
  return { year: Number(parts.year), month: Number(parts.month), day: Number(parts.day), hour: Number(parts.hour), minute: Number(parts.minute), second: Number(parts.second) };
}
function midnight(format: Intl.DateTimeFormat, date: number) {
  // Lower bound of this site's calendar date also handles 23/25-hour days and skipped midnight.
  let lo = date - 36 * 3600000, hi = date + 36 * 3600000;
  for (let step = 0; step < 36; ++step) {
    const middle = Math.floor((lo + hi) / 2 / 1000) * 1000;
    const fields = localFields(format, middle), localDate = Date.UTC(fields.year, fields.month - 1, fields.day);
    if (localDate < date) lo = middle + 1000; else hi = middle;
    if (lo >= hi) break;
  }
  return hi;
}
export function sunDay(latitude: number, longitude: number, timeZoneId: string, at = Date.now()): SunDay | null {
  if (!Number.isFinite(latitude) || latitude < -90 || latitude > 90 || !Number.isFinite(longitude) || longitude < -180 || longitude > 180 || !timeZoneId?.trim() || !Number.isFinite(at)) return null;
  let format: Intl.DateTimeFormat;
  try { format = new Intl.DateTimeFormat("en-GB", { timeZone: timeZoneId, year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit", hourCycle: "h23" }); format.format(at); }
  catch { return null; }
  const fields = localFields(format, at), date = Date.UTC(fields.year, fields.month - 1, fields.day);
  const start = midnight(format, date), end = midnight(format, date + 86400000), samples: SunPoint[] = [];
  for (let instant = start; instant < end; instant += 300000) samples.push(sunPosition(latitude, longitude, instant));
  samples.push(sunPosition(latitude, longitude, end));
  const paths: SunPoint[][] = []; let path: SunPoint[] = [], sunrise: number | null = null, sunset: number | null = null;
  if (samples[0]!.elevation >= 0) path.push(samples[0]!);
  for (let i = 1; i < samples.length; ++i) {
    const before = samples[i - 1]!, next = samples[i]!;
    if ((before.elevation >= 0) !== (next.elevation >= 0)) {
      let lo = before.at, hi = next.at;
      for (let step = 0; step < 18; ++step) { const middle = (lo + hi) / 2; if ((sunPosition(latitude, longitude, middle).elevation >= 0) === (before.elevation >= 0)) lo = middle; else hi = middle; }
      const crossing = sunPosition(latitude, longitude, (lo + hi) / 2);
      if (next.elevation >= 0) { sunrise = crossing.at; path.push(crossing); }
      else { sunset = crossing.at; path.push(crossing); paths.push(path); path = []; }
    }
    if (next.elevation >= 0) path.push(next);
  }
  if (path.length) paths.push(path);
  const now = sunPosition(latitude, longitude, at);
  return { date: `${fields.year}-${String(fields.month).padStart(2, "0")}-${String(fields.day).padStart(2, "0")}`, timeZoneId, startsAt: start, endsAt: end, paths, now: now.elevation >= 0 ? now : null, sunrise, sunset,
    state: samples.every(point => point.elevation >= 0) ? "polar-day" : samples.every(point => point.elevation < 0) ? "polar-night" : "normal" };
}
export function diagramRoofs(site: RoofSunSite): DiagramRoof[] {
  const valid = [1, 2].map(number => ({ number, capacity: number === 1 ? site.roof1Kwp : site.roof2Kwp, tilt: number === 1 ? site.roof1Tilt : site.roof2Tilt, azimuth: number === 1 ? site.roof1Azimuth : site.roof2Azimuth, panelCount: number === 1 ? site.roof1PanelCount : site.roof2PanelCount, panelsPerRow: number === 1 ? site.roof1PanelsPerRow : site.roof2PanelsPerRow }))
    .filter(roof => Number.isFinite(roof.capacity) && roof.capacity > 0 && roof.capacity <= 10000 && Number.isFinite(roof.tilt) && roof.tilt >= 0 && roof.tilt <= 90 && Number.isFinite(roof.azimuth) && roof.azimuth >= 0 && roof.azimuth <= 360);
  const maximum = Math.max(1e-9, ...valid.map(roof => roof.capacity));
  return valid.map((roof, index) => ({ ...roof, azimuth: roof.azimuth % 360, width: 24 + 18 * Math.sqrt(roof.capacity / maximum), depth: 12 + 24 * Math.cos(roof.tilt * rad), centerX: valid.length === 1 ? center : index === 0 ? 132 : 188 }));
}
export const sunPath = (points: SunPoint[]) => points.map((point, index) => `${index ? "L" : "M"}${point.x.toFixed(3)} ${point.y.toFixed(3)}`).join(" ");

export type Point3 = { x: number; y: number; z: number };
export type OrbitCamera = { yaw: number; elevation: number; zoom: number };
export type RoofFace3 = { id: string; kind: "roof" | "wall" | "vertical"; roofNumber?: number; points: Point3[]; normal: Point3; panels: Point3[]; panelTiles: Point3[][]; grid: Point3[][] };
export type RoofMesh = { faces: RoofFace3[]; ridge: Point3[][]; footprint: Point3[]; scale: number; hasVerticalPanels: boolean };
export type OrbitTouch = { pageX: number; pageY: number };
export const DEFAULT_ROOF_CAMERA: Readonly<OrbitCamera> = { yaw: -35, elevation: 32, zoom: 1 };
const clamp = (value: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, value));
type Point2 = { x: number; y: number };

/** Convex polygon clipped to a*x+b*y >= c. Shared intersections are calculated identically on both faces. */
function clip(poly: Point2[], a: number, b: number, c = 0): Point2[] {
  const out: Point2[] = [];
  for (let i = 0; i < poly.length; ++i) {
    const p = poly[i]!, q = poly[(i + 1) % poly.length]!, dp = a * p.x + b * p.y - c, dq = a * q.x + b * q.y - c;
    if (dp >= -1e-9) out.push(p);
    if ((dp > 1e-9 && dq < -1e-9) || (dp < -1e-9 && dq > 1e-9)) { const t = dp / (dp - dq); out.push({ x: p.x + (q.x - p.x) * t, y: p.y + (q.y - p.y) * t }); }
  }
  return out;
}
function section(poly: Point2[], a: number, b: number, c = 0): Point2[] {
  const points: Point2[] = [];
  const add = (p: Point2) => { if (!points.some(q => Math.hypot(p.x - q.x, p.y - q.y) < 1e-7)) points.push(p); };
  for (let i = 0; i < poly.length; ++i) {
    const p = poly[i]!, q = poly[(i + 1) % poly.length]!, dp = a * p.x + b * p.y - c, dq = a * q.x + b * q.y - c;
    if (Math.abs(dp) < 1e-9) add(p);
    if (dp * dq < 0) { const t = dp / (dp - dq); add({ x: p.x + (q.x - p.x) * t, y: p.y + (q.y - p.y) * t }); }
  }
  return points.slice(0, 2);
}

/** Exact-count rectangular modules in an orthonormal roof plane. The complete
 * regular-row block is fitted inside every convex edge, never clipped or culled.
 * Missing counts remain unknown rather than being inferred from installed kWp.
 */
export function roofPanelTiles(points: Point3[], normal: Point3, tilt: number, azimuth: number, count?: number | null, requestedColumns?: number | null): Point3[][] {
  if (!Number.isInteger(count) || count! <= 0 || count! > 1000 || points.length < 3) return [];
  const az = azimuth * rad, pitch = tilt * rad;
  const u = { x: Math.cos(az), y: -Math.sin(az), z: 0 }, v = { x: Math.sin(az) * Math.cos(pitch), y: Math.cos(az) * Math.cos(pitch), z: -Math.sin(pitch) }, origin = points[0]!;
  const uv = points.map(p => ({ x: (p.x - origin.x) * u.x + (p.y - origin.y) * u.y + (p.z - origin.z) * u.z, y: (p.x - origin.x) * v.x + (p.y - origin.y) * v.y + (p.z - origin.z) * v.z }));
  const center = { x: uv.reduce((sum, p) => sum + p.x, 0) / uv.length, y: uv.reduce((sum, p) => sum + p.y, 0) / uv.length };
  const width = Math.max(...uv.map(p => p.x)) - Math.min(...uv.map(p => p.x)), height = Math.max(...uv.map(p => p.y)) - Math.min(...uv.map(p => p.y));
  if (!(width > 0 && height > 0)) return [];
  const columns = Number.isInteger(requestedColumns) && requestedColumns! > 0 && requestedColumns! <= count! ? requestedColumns! : clamp(Math.round(Math.sqrt(count! * 1.7 * width / height)), 1, count!);
  const rows = Math.ceil(count! / columns), blockWidth = columns + (columns - 1) * .15, blockHeight = rows * 1.7 + (rows - 1) * .15;
  const signedArea = uv.reduce((sum, p, i) => { const q = uv[(i + 1) % uv.length]!; return sum + p.x * q.y - q.x * p.y; }, 0), direction = signedArea >= 0 ? 1 : -1;
  let scale = Infinity;
  for (let i = 0; i < uv.length; ++i) {
    const p = uv[i]!, q = uv[(i + 1) % uv.length]!, a = -(q.y - p.y) * direction, b = (q.x - p.x) * direction;
    const extent = Math.abs(a) * blockWidth / 2 + Math.abs(b) * blockHeight / 2;
    if (extent > 0) scale = Math.min(scale, (a * (center.x - p.x) + b * (center.y - p.y)) / extent);
  }
  if (!(Number.isFinite(scale) && scale > 0)) return [];
  scale *= .88;
  const project = (x: number, y: number): Point3 => ({ x: origin.x + u.x * (center.x + x * scale) + v.x * (center.y + y * scale) + normal.x * .3, y: origin.y + u.y * (center.x + x * scale) + v.y * (center.y + y * scale) + normal.y * .3, z: origin.z + u.z * (center.x + x * scale) + v.z * (center.y + y * scale) + normal.z * .3 });
  return Array.from({ length: count! }, (_, index) => {
    const row = Math.floor(index / columns), rowLength = Math.min(columns, count! - row * columns), x = -(rowLength + (rowLength - 1) * .15) / 2 + index % columns * 1.15, y = -blockHeight / 2 + row * 1.85;
    return [project(x, y), project(x + 1, y), project(x + 1, y + 1.7), project(x, y + 1.7)];
  });
}

/** Schematic connected building, in East/North/Up coordinates. Module count never changes roof pitch.
 * Two upward roof planes form their lower envelope on one footprint; their intersection is the shared ridge.
 * Exact vertical arrays cannot bound a roof height, so they stand on the shared flat/remaining roof instead.
 * A single uniform scale fits even near-vertical roofs without falsifying their configured angles.
 */
export function roofMesh(site: RoofSunSite): RoofMesh {
  const roofs = diagramRoofs(site), faces: RoofFace3[] = [], ridge: Point3[][] = [];
  if (!roofs.length) return { faces, ridge, footprint: [], scale: 1, hasVerticalPanels: false };
  const bearing = (roofs.find(roof => roof.tilt < 90) ?? roofs[0]!).azimuth * rad;
  // r points along the ridge, d down the first slope. Their basis is reflected,
  // so clockwise local corners become counterclockwise world corners.
  const footprint = [[-40, -32], [-40, 32], [40, 32], [40, -32]].map(([u, v]) => ({ x: -Math.cos(bearing) * u! + Math.sin(bearing) * v!, y: Math.sin(bearing) * u! + Math.cos(bearing) * v! }));
  const maximum = Math.max(...roofs.map(roof => roof.capacity));
  const planes = roofs.filter(roof => roof.tilt < 90).map(roof => ({ ...roof, tx: Math.sin(roof.azimuth * rad) * Math.tan(roof.tilt * rad), ty: Math.cos(roof.azimuth * rad) * Math.tan(roof.tilt * rad) }));
  const height = 14 + Math.max(0, ...planes.flatMap(plane => footprint.map(p => Math.abs(plane.tx * p.x + plane.ty * p.y))));
  const roofZ = (p: Point2) => planes.length ? Math.min(...planes.map(plane => height - plane.tx * p.x - plane.ty * p.y)) : 14;
  let splitX = 0, splitY = 0;
  if (planes.length === 2) {
    splitX = planes[0]!.tx - planes[1]!.tx; splitY = planes[0]!.ty - planes[1]!.ty;
    if (Math.hypot(splitX, splitY) < 1e-9) { splitX = Math.sin(planes[0]!.azimuth * rad); splitY = Math.cos(planes[0]!.azimuth * rad); }
    else ridge.push(section(footprint, splitX, splitY).map(p => ({ ...p, z: roofZ(p) })));
  }
  for (const [index, plane] of planes.entries()) {
    const poly = planes.length === 2 ? clip(footprint, splitX * (index ? -1 : 1), splitY * (index ? -1 : 1)) : footprint;
    const z = (p: Point2, offset = 0) => ({ ...p, z: height - plane.tx * p.x - plane.ty * p.y + offset });
    const points = poly.map(p => z(p)), normal = { x: Math.sin(plane.tilt * rad) * Math.sin(plane.azimuth * rad), y: Math.sin(plane.tilt * rad) * Math.cos(plane.azimuth * rad), z: Math.cos(plane.tilt * rad) };
    faces.push({ id: `roof-${plane.number}`, kind: "roof", roofNumber: plane.number, points, normal, panels: [], panelTiles: roofPanelTiles(points, normal, plane.tilt, plane.azimuth, plane.panelCount, plane.panelsPerRow), grid: [] });
  }
  if (!planes.length) faces.push({ id: "flat-base", kind: "roof", points: footprint.map(p => ({ ...p, z: 14 })), normal: { x: 0, y: 0, z: 1 }, panels: [], panelTiles: [], grid: [] });
  for (let i = 0; i < footprint.length; ++i) {
    const a = footprint[i]!, b = footprint[(i + 1) % footprint.length]!, cuts = [a];
    if (planes.length === 2) { const da = splitX * a.x + splitY * a.y, db = splitX * b.x + splitY * b.y; if (da * db < -1e-9) { const t = da / (da - db); cuts.push({ x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t }); } }
    cuts.push(b);
    for (let j = 1; j < cuts.length; ++j) {
      const p = cuts[j - 1]!, q = cuts[j]!, length = Math.hypot(q.x - p.x, q.y - p.y);
      faces.push({ id: `wall-${i}-${j}`, kind: "wall", points: [{ ...p, z: 0 }, { ...q, z: 0 }, { ...q, z: roofZ(q) }, { ...p, z: roofZ(p) }], normal: { x: (q.y - p.y) / length, y: (p.x - q.x) / length, z: 0 }, panels: [], panelTiles: [], grid: [] });
    }
  }
  for (const roof of roofs.filter(roof => roof.tilt === 90)) {
    const az = roof.azimuth * rad, tangent = { x: Math.cos(az), y: -Math.sin(az) }, center = { x: roofs.length === 1 ? 0 : roof.number === 1 ? -14 : 14, y: 0 }, half = 14 * Math.sqrt(roof.capacity / maximum);
    const a = { x: center.x - tangent.x * half, y: center.y - tangent.y * half }, b = { x: center.x + tangent.x * half, y: center.y + tangent.y * half }, base = Math.max(roofZ(a), roofZ(b)) + .5;
    const points = [{ ...a, z: base }, { ...b, z: base }, { ...b, z: base + 24 }, { ...a, z: base + 24 }];
    const grid = [[{ ...a, z: roofZ(a) }, points[0]!], [{ ...b, z: roofZ(b) }, points[1]!]], normal = { x: Math.sin(az), y: Math.cos(az), z: 0 };
    faces.push({ id: `vertical-${roof.number}`, kind: "vertical", roofNumber: roof.number, points, normal, panels: [], panelTiles: roofPanelTiles(points, normal, 90, roof.azimuth, roof.panelCount, roof.panelsPerRow), grid });
  }
  const scale = 112 / Math.max(Math.max(...footprint.map(p => p.x)) - Math.min(...footprint.map(p => p.x)), Math.max(...footprint.map(p => p.y)) - Math.min(...footprint.map(p => p.y)), ...faces.flatMap(face => face.points.map(p => p.z)));
  const fit = (p: Point3): Point3 => ({ x: p.x * scale, y: p.y * scale, z: p.z * scale });
  return { faces: faces.map(face => ({ ...face, points: face.points.map(fit), panels: face.panels.map(fit), panelTiles: face.panelTiles.map(tile => tile.map(fit)), grid: face.grid.map(line => line.map(fit)) })), ridge: ridge.map(line => line.map(fit)), footprint: footprint.map(p => fit({ ...p, z: 0 })), scale, hasVerticalPanels: roofs.some(roof => roof.tilt === 90) };
}

export function normalizeRoofCamera(camera: OrbitCamera): OrbitCamera {
  return { yaw: Number.isFinite(camera.yaw) ? ((camera.yaw + 180) % 360 + 360) % 360 - 180 : DEFAULT_ROOF_CAMERA.yaw, elevation: Number.isFinite(camera.elevation) ? clamp(camera.elevation, 10, 85) : DEFAULT_ROOF_CAMERA.elevation, zoom: Number.isFinite(camera.zoom) ? clamp(camera.zoom, .7, 2.5) : 1 };
}
export function projectRoofPoint(point: Point3, camera: OrbitCamera): Point3 {
  const view = normalizeRoofCamera(camera), yaw = view.yaw * rad, elevation = view.elevation * rad, horizontal = point.x * Math.sin(yaw) + point.y * Math.cos(yaw);
  return { x: 160 + (point.x * Math.cos(yaw) - point.y * Math.sin(yaw)) * view.zoom, y: 180 + (horizontal * Math.sin(elevation) - point.z * Math.cos(elevation)) * view.zoom, z: horizontal * Math.cos(elevation) + point.z * Math.sin(elevation) };
}
export const roofPath3 = (points: Point3[], camera: OrbitCamera, close = false) => points.map((point, index) => { const p = projectRoofPoint(point, camera); return `${index ? "L" : "M"}${p.x.toFixed(3)} ${p.y.toFixed(3)}`; }).join(" ") + (close ? " Z" : "");
export function sunPoint3(point: Pick<SunPoint, "azimuth" | "elevation">, distance = 110): Point3 { const altitude = point.elevation * rad, bearing = point.azimuth * rad; return { x: Math.sin(bearing) * Math.cos(altitude) * distance, y: Math.cos(bearing) * Math.cos(altitude) * distance, z: Math.sin(altitude) * distance }; }
export function orbitRoofGesture(camera: OrbitCamera, previous: readonly OrbitTouch[], current: readonly OrbitTouch[]): OrbitCamera {
  if (!previous.length || previous.length !== current.length || current.some(p => !Number.isFinite(p.pageX) || !Number.isFinite(p.pageY)) || previous.some(p => !Number.isFinite(p.pageX) || !Number.isFinite(p.pageY))) return camera;
  if (current.length >= 2) { const before = Math.hypot(previous[1]!.pageX - previous[0]!.pageX, previous[1]!.pageY - previous[0]!.pageY), after = Math.hypot(current[1]!.pageX - current[0]!.pageX, current[1]!.pageY - current[0]!.pageY); return before > 1 && after > 1 ? normalizeRoofCamera({ ...camera, zoom: camera.zoom * after / before }) : camera; }
  return normalizeRoofCamera({ ...camera, yaw: camera.yaw + (current[0]!.pageX - previous[0]!.pageX) * .6, elevation: camera.elevation - (current[0]!.pageY - previous[0]!.pageY) * .35 });
}
