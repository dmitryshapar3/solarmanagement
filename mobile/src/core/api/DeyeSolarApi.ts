import { ApiClient } from "./ApiClient";
import {
  AuthResponse,
  Dashboard,
  Device,
  DeyeDevice,
  DeyeStation,
  DisplaySettings,
  DeyeCloudSettings,
  PollingSettings,
  Reading,
  Rule,
  RuleRequest,
  RuleRunLog,
  Settings,
  ShellySettings,
  SocketStateResponse
} from "./types";

type DeyeDeviceSelectionRequest = {
  stationId: number;
  serialNumber: string;
};

export class DeyeSolarApi {
  constructor(private readonly client: ApiClient) {}

  login(username: string, password: string): Promise<AuthResponse> {
    return this.client.request<AuthResponse>("/api/auth/login", {
      method: "POST",
      body: { username, password }
    });
  }

  logout(): Promise<void> {
    return this.client.request<void>("/api/auth/logout", { method: "POST" });
  }

  getDashboard(): Promise<Dashboard> {
    return this.client.request<Dashboard>("/api/dashboard");
  }

  getDevices(refresh = false): Promise<Device[]> {
    return this.client.request<Device[]>("/api/devices", {
      query: { refresh }
    });
  }

  setDeviceState(entityId: string, isOn: boolean): Promise<SocketStateResponse> {
    return this.client.request<SocketStateResponse>("/api/devices/state", {
      method: "POST",
      body: { entityId, isOn }
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
