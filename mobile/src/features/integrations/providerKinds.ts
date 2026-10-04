import type { IntegrationProvider } from "../../core/api/IntegrationApi";

export function supportsDeviceKind(provider: IntegrationProvider, kind: "inverter" | "socket"): boolean {
  return provider.uiLayout?.steps.some(step => step.id === kind) === true
    || kind === "inverter" && provider.providerId === "deye.cloud"
    || kind === "socket" && provider.providerId === "shelly.cloud";
}
