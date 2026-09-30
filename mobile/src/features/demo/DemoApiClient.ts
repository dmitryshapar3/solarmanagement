import { ApiClient, ApiError, type RequestOptions } from "../../core/api/ApiClient";
import type { DeyeCloudSettings, DisplaySettings, PollingSettings, Rule, RuleRequest, ShellySettings } from "../../core/api/types";
import {
  createDemoState, DEMO_API_BASE_URL, DEMO_USERNAME, demoEstimate, demoInverter, demoReadings, demoSales, demoSolarHistory, type DemoState
} from "./fixtures";

/** A separate offline installation. No request, credential, or command can reach a server. */
export class DemoApiClient extends ApiClient {
  private readonly state: DemoState;
  private active = true;
  private nextRuleId = 3;

  constructor(private readonly clock: () => Date = () => new Date()) {
    super({ baseUrl: DEMO_API_BASE_URL, transport: async () => { throw new Error("Demo transport is disabled."); } });
    this.state = createDemoState(clock());
  }

  override setBaseUrl(_baseUrl: string): void {
    throw new Error("Leave demo mode before connecting to a server.");
  }

  override setToken(_token?: string | null): void {
    // Demo never accepts or forwards a real bearer token.
  }

  override async request<T>(path: string, options: RequestOptions = {}): Promise<T> {
    if (options.signal?.aborted) {
      const error = new Error("The request was canceled.");
      error.name = "AbortError";
      throw error;
    }
    const value = this.dispatch(path, options);
    // Screens receive snapshots; editing a returned object cannot mutate the installation.
    return (value === undefined ? undefined : JSON.parse(JSON.stringify(value))) as T;
  }

  private dispatch(path: string, options: RequestOptions): unknown {
    const method = options.method ?? "GET";
    const route = `${method} ${path}`;
    const now = this.clock();
    const timeZone = this.state.settings.display.timeZoneId;
    if (route === "POST /api/auth/login") throw new ApiError(400, "Demo mode does not require a username or password.");
    if (route === "GET /api/auth/session") return { authenticated: this.active, username: this.active ? DEMO_USERNAME : null };
    if (route === "POST /api/auth/logout") { this.active = false; return; }
    if (!this.active) throw new ApiError(401, "The demo session has ended.");

    if (route === "GET /api/dashboard" || route === "POST /api/dashboard/refresh") return {
      inverter: demoInverter(now, timeZone), devicesLoaded: true, deviceLastUpdated: now.toISOString(),
      devices: this.state.devices, manualDevices: this.state.devices, rules: this.state.rules, timeZoneId: timeZone
    };
    if (route === "GET /api/solar/estimate") return demoEstimate(now, timeZone);
    if (route === "GET /api/solar/history") {
      const period = options.query?.period ?? "Today";
      if (period !== "Today" && period !== "Week" && period !== "Month") throw new ApiError(400, "Select a valid generation period.");
      return demoSolarHistory(period, optionalDate(options.query?.date), now, timeZone);
    }
    if (route === "GET /api/sales") {
      const period = options.query?.period ?? "Day";
      if (period !== "Day" && period !== "Month" && period !== "Year" && period !== "Custom") throw new ApiError(400, "Select a valid sales period.");
      const date = optionalDate(options.query?.date) ?? localDate(now, timeZone);
      return demoSales(period, date, now, timeZone);
    }
    if (route === "GET /api/devices") return { devices: this.state.devices, lastUpdated: now.toISOString() };
    if (route === "POST /api/devices/state") {
      const body = objectBody(options.body);
      const device = this.state.devices.find(item => item.id === body.entityId);
      if (!device) throw new ApiError(404, "Demo device not found.");
      if (typeof body.isOn !== "boolean") throw new ApiError(400, "Choose an on/off state.");
      device.isOn = body.isOn;
      device.currentPowerW = body.isOn ? device.id.endsWith("lamp") ? 45 : 850 : 0;
      for (const rule of this.state.rules.filter(item => item.entityId === device.id)) {
        rule.currentState = body.isOn;
        rule.currentStateChangedAt = now.toISOString();
      }
      const inverter = demoInverter(now, timeZone);
      this.state.runs.unshift({
        id: Math.max(0, ...this.state.runs.map(item => item.id)) + 1,
        timestamp: now.toISOString(), ruleName: "Demo manual override", action: body.isOn ? "ON" : "OFF",
        conditionKey: "demo-manual", reason: "Simulated command; no hardware was contacted.",
        batterySoc: inverter.batterySoc, solarProduction: inverter.solarProduction, batteryPower: inverter.batteryPower
      });
      this.state.runs = this.state.runs.slice(0, 500);
      return { entityId: device.id, isOn: device.isOn, device };
    }
    if (route === "GET /api/rules") return this.state.rules;
    if (route === "POST /api/rules") {
      const request = this.ruleRequest(options.body);
      const device = this.state.devices.find(item => item.id === request.entityId)!;
      const rule: Rule = { ...request, id: this.nextRuleId++, currentState: device.isOn, currentStateChangedAt: now.toISOString(), lastEvaluated: now.toISOString() };
      this.state.rules.push(rule);
      return rule;
    }
    const ruleRoute = /^\/api\/rules\/(\d+)(\/enabled)?$/.exec(path);
    if (ruleRoute) {
      const id = Number(ruleRoute[1]);
      const index = this.state.rules.findIndex(rule => rule.id === id);
      const rule = this.state.rules[index];
      if (!rule) throw new ApiError(404, "Demo rule not found.");
      if (method === "GET" && !ruleRoute[2]) return rule;
      if (method === "PUT" && !ruleRoute[2]) {
        this.state.rules[index] = { ...rule, ...this.ruleRequest(options.body) };
        return this.state.rules[index];
      }
      if (method === "PATCH" && ruleRoute[2]) {
        const { enabled } = objectBody(options.body);
        if (typeof enabled !== "boolean") throw new ApiError(400, "Choose an enabled state.");
        rule.enabled = enabled;
        return rule;
      }
      if (method === "DELETE" && !ruleRoute[2]) { this.state.rules.splice(index, 1); return; }
    }
    if (route === "GET /api/readings") return demoReadings(historyHours(options.query?.hours), now, timeZone);
    if (route === "GET /api/rule-runs") {
      const cutoff = now.getTime() - historyHours(options.query?.hours) * 3600000;
      const filter = String(options.query?.filter ?? "ALL").toUpperCase();
      return this.state.runs.filter(run => Date.parse(run.timestamp) >= cutoff &&
        (filter === "ON" || filter === "OFF" ? run.action === filter : filter === "CHANGES" ? run.action !== "NO_CHANGE" : true));
    }
    if (route === "GET /api/settings") return this.state.settings;
    if (route === "PUT /api/settings/deye") {
      const value = objectBody(options.body) as unknown as DeyeCloudSettings;
      requireStrings(value, ["baseUrl", "appId", "appSecret", "email", "password", "deviceSn"]);
      requireNumber(value.stationId, 0, Number.MAX_SAFE_INTEGER, "station ID");
      this.state.settings.deyeCloud = { ...value };
      return;
    }
    if (route === "GET /api/settings/deye/stations") return this.state.stations;
    const stationRoute = /^\/api\/settings\/deye\/stations\/(\d+)\/devices$/.exec(path);
    if (method === "GET" && stationRoute) return this.state.inverters.filter(device => device.stationId === Number(stationRoute[1]));
    if (route === "POST /api/settings/deye/selected-device") {
      const body = objectBody(options.body);
      const inverter = this.state.inverters.find(device => device.stationId === body.stationId && device.serialNumber === body.serialNumber);
      if (!inverter) throw new ApiError(404, "Demo inverter not found.");
      this.state.settings.deyeCloud = { ...this.state.settings.deyeCloud, stationId: inverter.stationId, deviceSn: inverter.serialNumber };
      return this.state.settings.deyeCloud;
    }
    if (route === "PUT /api/settings/shelly") {
      const value = objectBody(options.body) as unknown as ShellySettings;
      requireStrings(value, ["serverUri", "authKey", "deviceId"]);
      requireNumber(value.requestIntervalMilliseconds, 100, 60000, "request interval");
      this.state.settings.shelly = { ...value };
      return;
    }
    if (route === "POST /api/settings/socket/selected-device") {
      const { entityId } = objectBody(options.body);
      const device = this.state.devices.find(item => item.id === entityId);
      if (!device) throw new ApiError(404, "Demo socket not found.");
      this.state.settings.shelly.deviceId = device.id.replace(/^shelly:/, "");
      return this.state.settings;
    }
    if (route === "PUT /api/settings/polling") {
      const value = objectBody(options.body) as unknown as PollingSettings;
      requireNumber(value.intervalSeconds, 5, 300, "polling interval");
      this.state.settings.polling = { intervalSeconds: value.intervalSeconds };
      return;
    }
    if (route === "PUT /api/settings/display") {
      const value = objectBody(options.body) as unknown as DisplaySettings;
      requireStrings(value, ["timeZoneId"]);
      try { new Intl.DateTimeFormat("en-GB", { timeZone: value.timeZoneId }); }
      catch { throw new ApiError(400, "Enter a valid timezone."); }
      this.state.settings.display = { timeZoneId: value.timeZoneId };
      return;
    }
    throw new ApiError(404, "This operation is not available in the offline demo.");
  }

  private ruleRequest(body: unknown): RuleRequest {
    const value = objectBody(body) as unknown as RuleRequest;
    requireStrings(value, ["name", "entityId"]);
    if (!value.name.trim() || !this.state.devices.some(device => device.id === value.entityId)) throw new ApiError(400, "Enter a rule name and choose a demo device.");
    for (const key of ["enabled", "useSeparateSocTurnOffThreshold", "useSolarProductionThreshold"] as const) {
      if (typeof value[key] !== "boolean") throw new ApiError(400, "Enter valid rule options.");
    }
    requireNumber(value.socTurnOnThreshold, 0, 100, "SOC threshold");
    requireNumber(value.socTurnOffThreshold, 0, 100, "SOC threshold");
    requireNumber(value.minAverageSolarProductionWatts, 0, 30000, "solar threshold");
    requireNumber(value.cooldownMinutes, 1, 240, "cooldown");
    requireNumber(value.intervalSeconds, 10, 3600, "rule interval");
    for (const time of [value.activeFrom, value.activeTo]) {
      if (time !== null && (typeof time !== "string" || !/^([01]\d|2[0-3]):[0-5]\d$/.test(time))) throw new ApiError(400, "Use HH:mm for the time window.");
    }
    if (Boolean(value.activeFrom) !== Boolean(value.activeTo)) throw new ApiError(400, "Set both time-window values or leave both empty.");
    if (value.useSeparateSocTurnOffThreshold && value.socTurnOffThreshold > value.socTurnOnThreshold) throw new ApiError(400, "Turn OFF SOC cannot exceed turn ON SOC.");
    // Store only known configuration fields, not caller-provided state or IDs.
    return {
      name: value.name.trim(), entityId: value.entityId, enabled: value.enabled,
      socTurnOnThreshold: value.socTurnOnThreshold, useSeparateSocTurnOffThreshold: value.useSeparateSocTurnOffThreshold,
      socTurnOffThreshold: value.socTurnOffThreshold, useSolarProductionThreshold: value.useSolarProductionThreshold,
      minAverageSolarProductionWatts: value.minAverageSolarProductionWatts, cooldownMinutes: value.cooldownMinutes,
      intervalSeconds: value.intervalSeconds, activeFrom: value.activeFrom, activeTo: value.activeTo
    };
  }
}

function objectBody(value: unknown): Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) throw new ApiError(400, "Enter valid demo settings.");
  return value as Record<string, unknown>;
}

function requireStrings(value: object, keys: string[]): void {
  for (const key of keys) if (typeof (value as Record<string, unknown>)[key] !== "string") throw new ApiError(400, "Enter valid demo values.");
}

function requireNumber(value: unknown, min: number, max: number, name: string): void {
  if (typeof value !== "number" || !Number.isFinite(value) || value < min || value > max) throw new ApiError(400, `Enter a valid ${name}.`);
}

function historyHours(value: unknown): number {
  const hours = Number(value ?? 6);
  return Number.isFinite(hours) ? Math.min(168, Math.max(1, hours)) : 6;
}

function optionalDate(value: unknown): string | undefined {
  if (value === undefined || value === null) return undefined;
  if (typeof value !== "string" || !/^\d{4}-\d{2}-\d{2}$/.test(value)) throw new ApiError(400, "Select a valid date.");
  const date = new Date(`${value}T12:00:00Z`);
  if (!Number.isFinite(date.getTime()) || date.toISOString().slice(0, 10) !== value) throw new ApiError(400, "Select a valid date.");
  return value;
}

function localDate(now: Date, timeZone: string): string {
  const parts = new Intl.DateTimeFormat("en-GB", { timeZone, year: "numeric", month: "2-digit", day: "2-digit" }).formatToParts(now);
  return ["year", "month", "day"].map(name => parts.find(part => part.type === name)?.value).join("-");
}
