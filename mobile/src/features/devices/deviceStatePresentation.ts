import type { Device } from "../../core/api/types";

export function deviceStatePresentation(device: Pick<Device, "online" | "stateKnown" | "isOn">) {
  if (!device.online) return { label: "Offline", tone: "neutral" } as const;
  if (device.stateKnown !== true) return { label: "State unavailable", tone: "neutral" } as const;
  return device.isOn ? { label: "ON", tone: "success" } as const : { label: "OFF", tone: "warning" } as const;
}
