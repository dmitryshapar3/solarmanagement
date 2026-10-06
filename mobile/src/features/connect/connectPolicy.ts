import type { DiscoveredIntegrationDevice, IntegrationDeviceBinding } from "../../core/api/IntegrationApi";
import type { BillingAccess } from "../subscription/billingPolicy";

export function discoveryFacts(device: DiscoveredIntegrationDevice) {
  const metadata = device.metadata;
  const capabilities = metadata?.capabilities;
  const canSwitch = capabilities && typeof capabilities === "object" && !Array.isArray(capabilities)
    ? (capabilities as Record<string, unknown>).canSwitch : undefined;
  return {
    canSwitch: typeof canSwitch === "boolean" ? canSwitch : null,
    online: typeof metadata?.online === "boolean" ? metadata.online : null
  };
}

export function matchingBinding(device: DiscoveredIntegrationDevice, bindings: IntegrationDeviceBinding[]) {
  return bindings.find(binding => binding.kind === device.kind && binding.remoteId === device.remoteId
    && (binding.channel ?? "") === (device.channel ?? ""));
}

export function trialSocketRemaining(access: BillingAccess | null | undefined): number | null {
  return access?.status === "trial" && access.socketLimit === 1 && typeof access.socketUsage === "number"
    && Number.isSafeInteger(access.socketUsage) && access.socketUsage >= 0
    ? Math.max(0, access.socketLimit - access.socketUsage) : null;
}

export function canSelectSocket(access: BillingAccess | null | undefined): boolean {
  if (!access?.hasAccess) return false;
  if (access.status === "active") return true;
  const remaining = trialSocketRemaining(access);
  return access.status === "trial" && remaining !== null && remaining > 0;
}
