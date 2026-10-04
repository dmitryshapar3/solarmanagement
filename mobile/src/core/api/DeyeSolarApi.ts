import { ApiClient, ApiError } from "./ApiClient";
import { IntegrationApi } from "./IntegrationApi";
import { SocketCommandCoordinator } from "./SocketCommandCoordinator";
import {
  AuthResponse,
  Dashboard,
  DeviceList,
  DeyeDevice,
  DeyeStation,
  DisplaySettings,
  DeyeCloudSettings,
  PollingSettings,
  Reading,
  Rule,
  RuleRequest,
  RuleRunLog,
  SessionResponse,
  Settings,
  ShellySettings,
  SocketStateResponse
} from "./types";
import type { Device, IntegrationKind, IntegrationTestRequest, IntegrationTestResult } from "./types";
import type { AuthOptions, VerificationChannel, VerificationPurpose, VerificationResponse } from "./types";
import type { SolarSiteSettings } from "./types";
import { ExportSalesPeriod, ExportSalesResult, SolarEstimateState, SolarHistoryPeriod, SolarHistoryResult } from "./types";

type DeyeDeviceSelectionRequest = {
  stationId: number;
  serialNumber: string;
};

export class DeyeSolarApi {
  readonly integrations: IntegrationApi;
  readonly socketCommands: SocketCommandCoordinator;

  constructor(private readonly client: ApiClient) {
    this.integrations = new IntegrationApi(client);
    this.socketCommands = new SocketCommandCoordinator(this.integrations);
    client.onSessionChange(() => this.socketCommands.reset());
  }

  getAuthOptions(signal?: AbortSignal): Promise<AuthOptions> {
    return this.client.request("/api/auth/options", { signal, skipUnauthorizedHandler: true });
  }

  startVerification(channel: VerificationChannel, destination: string, purpose: VerificationPurpose, signal?: AbortSignal): Promise<VerificationResponse> {
    return this.client.request("/api/auth/verification/start", {
      method: "POST", body: { channel, destination, purpose }, signal, skipUnauthorizedHandler: true
    });
  }

  register(verificationId: string, code: string, password: string, signal?: AbortSignal): Promise<AuthResponse> {
    return this.client.request("/api/auth/register", {
      method: "POST", body: { verificationId, code, password }, signal, skipUnauthorizedHandler: true
    });
  }

  loginWithVerification(verificationId: string, code: string, signal?: AbortSignal): Promise<AuthResponse> {
    return this.client.request("/api/auth/verification/login", {
      method: "POST", body: { verificationId, code }, signal, skipUnauthorizedHandler: true
    });
  }

  exchangeGoogleCode(code: string, codeVerifier: string, signal?: AbortSignal): Promise<AuthResponse> {
    return this.client.request("/api/auth/google/exchange", {
      method: "POST", body: { code, codeVerifier }, signal, skipUnauthorizedHandler: true
    });
  }

  linkIdentity(verificationId: string, code: string): Promise<void> {
    return this.client.request("/api/auth/identities/link", { method: "POST", body: { verificationId, code } });
  }

  startGoogleLink(codeChallenge: string, state: string): Promise<{ authorizationUrl: string; expiresAt: string }> {
    return this.client.request("/api/auth/google/link/start", { method: "POST", body: { codeChallenge, state } });
  }

  async login(username: string, password: string, signal?: AbortSignal): Promise<AuthResponse> {
    try {
      return await this.client.request<AuthResponse>("/api/auth/login", {
        method: "POST",
        body: { username, password },
        skipUnauthorizedHandler: true,
        signal
      });
    } catch (ex) {
      if (ex instanceof ApiError && ex.status === 401) {
        throw new ApiError(401, "Invalid username or password.");
      }
      throw ex;
    }
  }

  getSession(signal?: AbortSignal): Promise<SessionResponse> {
    return this.client.request<SessionResponse>("/api/auth/session", { signal });
  }

  logout(signal?: AbortSignal): Promise<void> {
    return this.client.request<void>("/api/auth/logout", { method: "POST", signal });
  }

  getDashboard(signal?: AbortSignal): Promise<Dashboard> {
    return this.client.request<Dashboard>("/api/dashboard", { signal });
  }

  refreshDashboard(signal?: AbortSignal): Promise<Dashboard> {
    return this.client.request<Dashboard>("/api/dashboard/refresh", { method: "POST", signal });
  }

  getSolarEstimate(signal?: AbortSignal): Promise<SolarEstimateState> {
    return this.client.request<SolarEstimateState>("/api/solar/estimate", { signal });
  }

  getSolarHistory(period: SolarHistoryPeriod, date?: string, signal?: AbortSignal): Promise<SolarHistoryResult> {
    return this.client.request<SolarHistoryResult>("/api/solar/history", { query: { period, date }, signal });
  }

  getSales(period: ExportSalesPeriod, date: string, signal?: AbortSignal): Promise<ExportSalesResult> {
    return this.client.request<ExportSalesResult>("/api/sales", { query: { period, date }, signal });
  }

  getDevices(refresh = false): Promise<DeviceList> {
    return this.client.request<DeviceList>("/api/devices", {
      query: { refresh }
    });
  }

  setDeviceState(entityId: string, isOn: boolean): Promise<SocketStateResponse> {
    return this.client.request<SocketStateResponse>("/api/devices/state", {
      method: "POST",
      body: { entityId, isOn }
    });
  }

  renameDevice(entityId: string, name: string | null): Promise<Device> {
    return this.client.request<Device>(`/api/devices/${encodeURIComponent(entityId)}/name`, {
      method: "PATCH", body: { name }
    });
  }

  testIntegration(kind: IntegrationKind, body: IntegrationTestRequest = {}): Promise<IntegrationTestResult> {
    return this.client.request<IntegrationTestResult>(`/api/settings/test/${kind}`, {
      method: "POST", body, timeoutMs: 25000
    });
  }

  getRules(): Promise<Rule[]> {
    return this.client.request<Rule[]>("/api/rules");
  }

  getRule(id: number): Promise<Rule> {
    return this.client.request<Rule>(`/api/rules/${id}`);
  }

  createRule(rule: RuleRequest): Promise<Rule> {
    return this.client.request<Rule>("/api/rules", {
      method: "POST",
      body: rule
    });
  }

  updateRule(id: number, rule: RuleRequest): Promise<Rule> {
    return this.client.request<Rule>(`/api/rules/${id}`, {
      method: "PUT",
      body: rule
    });
  }

  setRuleEnabled(id: number, enabled: boolean): Promise<Rule> {
    return this.client.request<Rule>(`/api/rules/${id}/enabled`, {
      method: "PATCH",
      body: { enabled }
    });
  }

  deleteRule(id: number): Promise<void> {
    return this.client.request<void>(`/api/rules/${id}`, {
      method: "DELETE"
    });
  }

  getReadings(hours: number): Promise<Reading[]> {
    return this.client.request<Reading[]>("/api/readings", {
      query: { hours }
    });
  }

  getRuleRuns(hours: number, filter: string): Promise<RuleRunLog[]> {
    return this.client.request<RuleRunLog[]>("/api/rule-runs", {
      query: { hours, filter }
    });
  }

  getSettings(): Promise<Settings> {
    return this.client.request<Settings>("/api/settings");
  }

  getSiteSettings(): Promise<SolarSiteSettings> {
    return this.client.request("/api/settings/site");
  }

  saveSiteSettings(settings: SolarSiteSettings): Promise<void> {
    return this.client.request("/api/settings/site", { method: "PUT", body: settings });
  }

  saveDeyeCloud(settings: DeyeCloudSettings): Promise<void> {
    return this.client.request<void>("/api/settings/deye", {
      method: "PUT",
      body: settings
    });
  }

  fetchDeyeStations(): Promise<DeyeStation[]> {
    return this.client.request<DeyeStation[]>("/api/settings/deye/stations");
  }

  fetchDeyeDevices(stationId: number): Promise<DeyeDevice[]> {
    return this.client.request<DeyeDevice[]>(`/api/settings/deye/stations/${stationId}/devices`);
  }

  selectDeyeDevice(request: DeyeDeviceSelectionRequest): Promise<DeyeCloudSettings> {
    return this.client.request<DeyeCloudSettings>("/api/settings/deye/selected-device", {
      method: "POST",
      body: request
    });
  }

  saveShelly(settings: ShellySettings): Promise<void> {
    return this.client.request<void>("/api/settings/shelly", {
      method: "PUT",
      body: settings
    });
  }

  selectSocketDevice(entityId: string): Promise<Settings> {
    return this.client.request<Settings>("/api/settings/socket/selected-device", {
      method: "POST",
      body: { entityId }
    });
  }

  savePolling(settings: PollingSettings): Promise<void> {
    return this.client.request<void>("/api/settings/polling", {
      method: "PUT",
      body: settings
    });
  }

  saveDisplay(settings: DisplaySettings): Promise<void> {
    return this.client.request<void>("/api/settings/display", {
      method: "PUT",
      body: settings
    });
  }
}
