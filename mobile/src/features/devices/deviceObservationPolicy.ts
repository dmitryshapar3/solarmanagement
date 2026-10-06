import type { Device } from "../../core/api/types";
import type { DeviceDetails, DeviceHistory } from "../../core/api/redesignTypes";

/** Start of the current continuous observed state, never a command acknowledgement. */
export function currentObservedStateSince(device: Device, history?: DeviceHistory | null, now = Date.now()): string | null {
  if (!device.online || !device.stateKnown || !history?.intervals.length) return null;
  const end = Date.parse(history.end);
  if (!Number.isFinite(end) || end > now + 10_000 || now - end > 120_000) return null;
  const last = history.intervals.at(-1)!;
  if (Date.parse(last.to) !== end || last.isOn !== device.isOn || last.evidence !== "provider_observation") return null;
  let from = last.from;
  if (!Number.isFinite(Date.parse(from)) || Date.parse(from) >= end) return null;
  for (let index = history.intervals.length - 2; index >= 0; --index) {
    const previous = history.intervals[index]!;
    if (Date.parse(previous.to) !== Date.parse(from) || previous.isOn !== device.isOn
      || previous.evidence !== "provider_observation" || !Number.isFinite(Date.parse(previous.from))
      || Date.parse(previous.from) >= Date.parse(previous.to)) break;
    from = previous.from;
  }
  return from;
}

/** Bound SQL/HTTP fan-out; missing history does not hide the rest of the inventory. */
export async function readDeviceListHistories(devices: Device[], details: ReadonlyMap<string, DeviceDetails>,
  read: (id: string, signal: AbortSignal) => Promise<DeviceHistory>, signal: AbortSignal): Promise<Map<string, DeviceHistory>> {
  const eligible = devices.filter(device => device.online && device.stateKnown && details.get(device.id)?.supportsHistory);
  const histories = new Map<string, DeviceHistory>(); let next = 0;
  const cancelled = () => { if (signal.aborted) throw signal.reason ?? new Error("Device history read canceled."); };
  const worker = async () => {
    while (next < eligible.length) {
      cancelled(); const device = eligible[next++]!;
      try { const history = await read(device.id, signal); cancelled(); histories.set(device.id, history); }
      catch (error) { cancelled(); /* A partial history failure keeps its timestamp unavailable. */ }
    }
  };
  await Promise.all(Array.from({ length: Math.min(4, eligible.length) }, worker)); cancelled();
  return histories;
}
