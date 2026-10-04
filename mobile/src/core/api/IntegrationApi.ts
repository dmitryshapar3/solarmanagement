import type { ApiClient } from "./ApiClient";

export type IntegrationValue = string | number | boolean | null;
export type IntegrationField = {
  key: string;
  kind: string;
  label: string;
  required: boolean;
  secret: boolean;
  defaultValue?: IntegrationValue;
  minimum?: number | null;
  maximum?: number | null;
  options?: { value: string; label: string }[] | null;
};
export type IntegrationProvider = {
  providerId: string;
  packageVersion: string;
  packageDigest: string;
  descriptorDigest: string;
  displayName: string;
  uiContractVersion: number;
  configurationVersion: number;
  requiredUiFeatures: string[];
  fields: IntegrationField[];
  actions: string[];
};
export type IntegrationCatalog = { providers: IntegrationProvider[]; revision: string };
export type IntegrationInstance = {
  id: string;
  providerId: string;
  name: string;
  status: string;
  revision: number;
  generation: number;
  packageVersion: string;
  packageDigest: string;
  descriptorDigest: string;
};
export type IntegrationConfiguration = {
  instance: IntegrationInstance;
  values: Record<string, IntegrationValue>;
  secretPresent: Record<string, boolean>;
};
export type IntegrationVersion = {
  expectedRevision: number;
  packageVersion: string;
  packageDigest: string;
  descriptorDigest: string;
};
export type SecretOperation = { operation: "keep" | "replace" | "clear"; value?: string };
export type IntegrationConfigurationChange = IntegrationVersion & {
  values: Record<string, IntegrationValue>;
  secretOperations: Record<string, SecretOperation>;
};
export type IntegrationTest = { success: boolean; code: string; message: string };
export type DiscoveredIntegrationDevice = {
  selectionToken: string;
  name: string;
  kind: string;
  remoteId: string;
  channel?: string | null;
  metadata?: Record<string, IntegrationValue> | null;
};
export type IntegrationDiscovery = { devices: DiscoveredIntegrationDevice[]; expiresAt: string };
export type IntegrationDeviceBinding = {
  id: string;
  instanceId: string;
  kind: string;
  name: string;
  remoteId: string;
  channel?: string | null;
  isDefault: boolean;
};
export type SocketCommandReceipt = {
  commandId: string;
  deviceId: string;
  isOn: boolean;
  status: string;
  rejection: string | null;
  createdAt: string;
  completedAt: string | null;
};

// Provider setup may use the server's 300-second operation cap plus transport overhead.
const setupTimeoutMs = 330000;

/** Uses the existing authenticated transport, including session cancellation and endpoint binding. */
export class IntegrationApi {
  constructor(private readonly client: Pick<ApiClient, "request">) {}

  getCatalog(signal?: AbortSignal): Promise<IntegrationCatalog> {
    return this.client.request("/api/v2/integration-providers", { signal });
  }

  getInstances(signal?: AbortSignal): Promise<IntegrationInstance[]> {
    return this.client.request("/api/v2/integrations", { signal });
  }

  getProvider(providerId: string, packageVersion: string, signal?: AbortSignal): Promise<IntegrationProvider> {
    return this.client.request(`/api/v2/integration-providers/${encodeURIComponent(providerId)}/versions/${encodeURIComponent(packageVersion)}/ui`, { signal });
  }

  getProviderVersions(providerId: string, signal?: AbortSignal): Promise<IntegrationProvider[]> {
    return this.client.request(`/api/v2/integration-providers/${encodeURIComponent(providerId)}/versions`, { signal });
  }

  create(providerId: string, name: string, signal?: AbortSignal): Promise<IntegrationInstance> {
    return this.client.request("/api/v2/integrations", { method: "POST", body: { providerId, name }, signal });
  }

  getConfiguration(id: string, signal?: AbortSignal): Promise<IntegrationConfiguration> {
    return this.client.request(`${instancePath(id)}/configuration`, { signal });
  }

  saveConfiguration(id: string, body: IntegrationConfigurationChange, signal?: AbortSignal): Promise<IntegrationConfiguration> {
    return this.client.request(`${instancePath(id)}/configuration`, { method: "PUT", body, signal });
  }

  test(id: string, body: IntegrationConfigurationChange, signal?: AbortSignal): Promise<IntegrationTest> {
    return this.client.request(`${instancePath(id)}/test`, { method: "POST", body, signal, timeoutMs: setupTimeoutMs });
  }

  discover(id: string, body: IntegrationConfigurationChange, signal?: AbortSignal): Promise<IntegrationDiscovery> {
    return this.client.request(`${instancePath(id)}/discovery`, { method: "POST", body, signal, timeoutMs: setupTimeoutMs });
  }

  getDevices(id: string, signal?: AbortSignal): Promise<IntegrationDeviceBinding[]> {
    return this.client.request(`${instancePath(id)}/devices`, { signal });
  }

  selectDevice(id: string, draft: IntegrationConfigurationChange, selectionToken: string, signal?: AbortSignal): Promise<IntegrationDeviceBinding> {
    return this.client.request(`${instancePath(id)}/devices/selection`, { method: "POST", body: { draft, selectionToken }, signal });
  }

  setEnabled(id: string, enabled: boolean, body: IntegrationVersion, signal?: AbortSignal): Promise<IntegrationInstance> {
    return this.client.request(`${instancePath(id)}/${enabled ? "enable" : "disable"}`, { method: "POST", body, signal, timeoutMs: enabled ? setupTimeoutMs : undefined });
  }

  switchPackage(id: string, guard: IntegrationVersion, targetPackageVersion: string, signal?: AbortSignal): Promise<IntegrationInstance> {
    return this.client.request(`${instancePath(id)}/package`, { method: "POST", body: { guard, targetPackageVersion }, signal, timeoutMs: setupTimeoutMs });
  }

  sendDeviceCommand(deviceId: string, commandId: string, isOn: boolean): Promise<SocketCommandReceipt> {
    return this.client.request(`/api/v2/devices/${encodeURIComponent(deviceId)}/commands`, { method: "POST", body: { commandId, isOn }, timeoutMs: 25000 });
  }

  getDeviceCommand(deviceId: string, commandId: string): Promise<SocketCommandReceipt> {
    return this.client.request(`/api/v2/devices/${encodeURIComponent(deviceId)}/commands/${encodeURIComponent(commandId)}`);
  }

  getUnresolvedCommands(deviceId: string): Promise<SocketCommandReceipt[]> {
    return this.client.request(`/api/v2/devices/${encodeURIComponent(deviceId)}/commands`, { query: { unresolved: true } });
  }

  releaseDeviceCommand(deviceId: string, commandId: string): Promise<SocketCommandReceipt> {
    return this.client.request(`/api/v2/devices/${encodeURIComponent(deviceId)}/commands/${encodeURIComponent(commandId)}/release`, { method: "POST" });
  }
}

function instancePath(id: string): string {
  return `/api/v2/integrations/${encodeURIComponent(id)}`;
}
