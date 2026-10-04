import { AccountSecurityApi } from "./AccountSecurityApi";
import { ApiClient, ApiError } from "./ApiClient";
import { translate as t } from "../i18n";
import { IntegrationApi } from "./IntegrationApi";
import { SocketCommandCoordinator } from "./SocketCommandCoordinator";
import {
  AuthResponse,
  Dashboard,
  DeviceList,
  DisplaySettings,
  PollingSettings,
  Reading,
  Rule,
  RuleRequest,
  RuleRunLog,
  SessionResponse,
  Settings
} from "./types";
import type { Device, IntegrationKind, IntegrationTestRequest, IntegrationTestResult } from "./types";
import type { AuthOptions, VerificationChannel, VerificationPurpose, VerificationResponse } from "./types";
import type { SolarSiteSettings } from "./types";
import { ExportSalesPeriod, ExportSalesResult, SolarEstimateState, SolarHistoryPeriod, SolarHistoryResult } from "./types";
import { BillingAccess, readBillingAccess } from "../../features/subscription/billingPolicy";

export class DeyeSolarApi {
  getLanguage(signal?: AbortSignal): Promise<{ language: string | null }> {
    return this.client.request("/api/account/language", { signal });
  }
  setLanguage(language: string): Promise<{ language: string }> {
    return this.client.request("/api/account/language", { method: "PUT", body: { language } });
  }
  readonly integrations: IntegrationApi;
  readonly accountSecurity: AccountSecurityApi;
  readonly socketCommands: SocketCommandCoordinator;

  constructor(private readonly client: ApiClient) {
    this.integrations = new IntegrationApi(client);
    this.accountSecurity = new AccountSecurityApi(client);
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

  startGoogleLink(codeChallenge: string, state: string, signal?: AbortSignal): Promise<{ authorizationUrl: string; expiresAt: string }> {
    return this.client.request("/api/auth/google/link/start", { method: "POST", body: { codeChallenge, state }, signal });
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
        throw new ApiError(401, t("Invalid username or password."));
      }
      throw ex;
    }
  }

  getSession(signal?: AbortSignal): Promise<SessionResponse> {
    return this.client.request<SessionResponse>("/api/auth/session", { signal });
  }

  async getBillingAccess(signal?: AbortSignal): Promise<BillingAccess> {
    return readBillingAccess(await this.client.request("/api/billing/access", { signal }));
  }

  async verifyAppleTransaction(signedTransaction: string, signal?: AbortSignal): Promise<BillingAccess> {
    return readBillingAccess(await this.client.request("/api/billing/apple/verify", { method: "POST", body: { signedTransaction }, signal }));
  }

  onBillingDenied(observer: () => void): () => void {
    return this.client.onBillingDenied(observer);
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

  updateRule(id: number, rule: RuleRequest & { configurationVersion: string }): Promise<Rule> {
    return this.client.request<Rule>(`/api/rules/${id}`, {
      method: "PUT",
      body: rule
    });
  }

  setRuleEnabled(id: number, enabled: boolean, configurationVersion: string): Promise<Rule> {
    return this.client.request<Rule>(`/api/rules/${id}/enabled`, {
      method: "PATCH",
      body: { enabled, configurationVersion }
    });
  }

  deleteRule(id: number, configurationVersion: string): Promise<void> {
    return this.client.request<void>(`/api/rules/${id}`, {
      method: "DELETE", ifMatch: `"${configurationVersion}"`
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
