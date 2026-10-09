/** Approximate NOAA geometric solar position, clockwise from true north.
 * https://gml.noaa.gov/grad/solcalc/solareqns.PDF
 * Diagram only; no refraction, shadows, irradiance or energy prediction.
 * Horizon crossings use the solar centre at 0°, not apparent sunrise.
 */
export type SunPoint = { x: number; y: number; azimuth: number; elevation: number; at: number };
export type SunDay = { date: string; timeZoneId: string; startsAt: number; endsAt: number; paths: SunPoint[][]; now: SunPoint | null; sunrise: number | null; sunset: number | null; state: "normal" | "polar-day" | "polar-night" };
export type DiagramRoof = { number: number; capacity: number; tilt: number; azimuth: number; width: number; depth: number; centerX: number };
export type RoofSunSite = { latitude: number; longitude: number; timeZoneId: string; roof1Kwp: number; roof1Tilt: number; roof1Azimuth: number; roof2Kwp: number; roof2Tilt: number; roof2Azimuth: number };
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
  const valid = [1, 2].map(number => ({ number, capacity: number === 1 ? site.roof1Kwp : site.roof2Kwp, tilt: number === 1 ? site.roof1Tilt : site.roof2Tilt, azimuth: number === 1 ? site.roof1Azimuth : site.roof2Azimuth }))
    .filter(roof => Number.isFinite(roof.capacity) && roof.capacity > 0 && roof.capacity <= 10000 && Number.isFinite(roof.tilt) && roof.tilt >= 0 && roof.tilt <= 90 && Number.isFinite(roof.azimuth) && roof.azimuth >= 0 && roof.azimuth <= 360);
  const maximum = Math.max(1e-9, ...valid.map(roof => roof.capacity));
  return valid.map((roof, index) => ({ ...roof, azimuth: roof.azimuth % 360, width: 24 + 18 * Math.sqrt(roof.capacity / maximum), depth: 12 + 24 * Math.cos(roof.tilt * rad), centerX: valid.length === 1 ? center : index === 0 ? 132 : 188 }));
}
export const sunPath = (points: SunPoint[]) => points.map((point, index) => `${index ? "L" : "M"}${point.x.toFixed(3)} ${point.y.toFixed(3)}`).join(" ");
