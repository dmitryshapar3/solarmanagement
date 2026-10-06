export const compassPresets = [{ label: "N", value: 0 }, { label: "E", value: 90 }, { label: "S", value: 180 }, { label: "W", value: 270 }] as const;

export function compassPoint(bearing: number, radius = 22): { x: number; y: number } | null {
  // Older installations may store 360°. Display the same north direction while
  // new edits are separately validated against 0–359, without rewriting data.
  if (!Number.isFinite(bearing) || bearing < 0 || bearing > 360) return null;
  const radians = bearing * Math.PI / 180;
  return { x: 72 + radius * Math.sin(radians), y: 72 - radius * Math.cos(radians) };
}
