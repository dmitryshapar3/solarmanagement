import { translate as t } from "../i18n";
import type { ApiClient } from "./ApiClient";

export type IntegrationValue = string | number | boolean | null;
export type IntegrationUiCondition = { field: string; operator: string; value?: IntegrationValue };
export type IntegrationUiGroup = {
  id: string; title: string; instructions?: string | null; fieldKeys: string[]; actions: string[];
  activeWhen?: IntegrationUiCondition | null;
};
export type IntegrationUiStep = { id: string; title: string; instructions?: string | null; groups: IntegrationUiGroup[] };
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
  activeWhen?: IntegrationUiCondition | null;
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
  uiLayout?: { version: number; steps: IntegrationUiStep[] } | null;
  oauthDefinition?: { secretFieldKeys: string[] } | null;
};
export type IntegrationCatalog = { providers: IntegrationProvider[]; revision: string; providerKinds?: Record<string, ("inverter" | "socket")[]> };
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
  oauthFlowId?: string | null;
};
export type IntegrationOAuthStart = {
  flowId: string; authorizationUrl: string; expiresAt: string; returnUri: string; returnNonce: string;
};
export type IntegrationOAuthStatus = {
  flowId: string; status: string; expiresAt: string; values: Record<string, IntegrationValue>;
  secretPresent: Record<string, boolean>; code?: string | null;
};
export type IntegrationTest = { success: boolean; code: string; message: string };
export type DiscoveredIntegrationDevice = {
  selectionToken: string;
  name: string;
  kind: string;
  remoteId: string;
  channel?: string | null;
  metadata?: Record<string, unknown> | null;
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
  sourceInverterId?: string | null;
  phaseCount?: 1 | 3;
  displayName?: string | null;
};
export type IntegrationSourceInverter = { id: string; name: string; isDefault: boolean };
export type IntegrationSocketSourceChange = {
  guard: IntegrationVersion;
  sourceInverterId: string | null;
  phaseCount: 1 | 3;
  expectedSourceInverterId: string | null;
  expectedPhaseCount: 1 | 3;
};
export type SocketCommandReceipt = {
  commandId: string;
  deviceId: string;
  isOn: boolean;
  status: string;
  rejection: string | null;
  createdAt: string;
  completedAt: string | null;
  pausedRuleIds?: number[];
};
export type RuleConflictChoice = "pause" | "once";

// Provider setup may use the server's 300-second operation cap plus transport overhead.
const setupTimeoutMs = 330000;

/** Uses the existing authenticated transport, including session cancellation and endpoint binding. */
export class IntegrationApi {
  constructor(private readonly client: Pick<ApiClient, "request"> & Partial<Pick<ApiClient, "sessionEpoch" | "onSessionChange">>) {}

  get sessionEpoch(): number { return this.client.sessionEpoch ?? 0; }

  onSessionChange(observer: () => void): () => void { return this.client.onSessionChange?.(observer) ?? (() => {}); }

  startOAuth(id: string, draft: IntegrationConfigurationChange, signal?: AbortSignal): Promise<IntegrationOAuthStart> {
    return this.client.request(`${instancePath(id)}/oauth/start`, { method: "POST", body: { draft, client: "mobile" }, signal, timeoutMs: setupTimeoutMs });
  }

  getOAuth(id: string, flowId: string, signal?: AbortSignal): Promise<IntegrationOAuthStatus> {
    return this.client.request(`${instancePath(id)}/oauth/${encodeURIComponent(flowId)}`, { signal });
  }

  cancelOAuth(id: string, flowId: string): Promise<IntegrationOAuthStatus> {
    return this.client.request(`${instancePath(id)}/oauth/${encodeURIComponent(flowId)}/cancel`, { method: "POST" });
  }

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
  getSocketSources(signal?: AbortSignal): Promise<IntegrationSourceInverter[]> {
    return this.client.request("/api/v2/integration-socket-sources", { signal });
  }
  setSocketSource(id: string, deviceId: string, body: IntegrationSocketSourceChange, signal?: AbortSignal): Promise<IntegrationDeviceBinding> {
    return this.client.request(`${instancePath(id)}/devices/${encodeURIComponent(deviceId)}/source`, { method: "PUT", body, signal });
  }

  selectDevice(id: string, draft: IntegrationConfigurationChange, selectionToken: string, signal?: AbortSignal, displayName?: string): Promise<IntegrationDeviceBinding> {
    return this.client.request(`${instancePath(id)}/devices/selection`, { method: "POST", body: { draft, selectionToken, ...(displayName === undefined ? {} : { displayName }) }, signal });
  }

  setEnabled(id: string, enabled: boolean, body: IntegrationVersion, signal?: AbortSignal): Promise<IntegrationInstance> {
    return this.client.request(`${instancePath(id)}/${enabled ? "enable" : "disable"}`, { method: "POST", body, signal, timeoutMs: enabled ? setupTimeoutMs : undefined });
  }

  switchPackage(id: string, guard: IntegrationVersion, targetPackageVersion: string, signal?: AbortSignal): Promise<IntegrationInstance> {
    return this.client.request(`${instancePath(id)}/package`, { method: "POST", body: { guard, targetPackageVersion }, signal, timeoutMs: setupTimeoutMs });
  }

  sendDeviceCommand(deviceId: string, commandId: string, isOn: boolean, onRuleConflict?: RuleConflictChoice): Promise<SocketCommandReceipt> {
    return this.client.request(`/api/v2/devices/${encodeURIComponent(deviceId)}/commands`, { method: "POST", body: { commandId, isOn, ...(onRuleConflict ? { onRuleConflict } : {}) }, timeoutMs: 25000 });
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
