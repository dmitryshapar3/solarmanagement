import { translate as t } from "../../core/i18n";
import type { SocketCommandReceipt } from "../../core/api/IntegrationApi";
import { ApiClient, ApiError, type RequestOptions } from "../../core/api/ApiClient";
import type { DisplaySettings, PollingSettings, Rule, RuleRequest, SolarSiteSettings } from "../../core/api/types";
import {
  createDemoState, DEMO_API_BASE_URL, DEMO_USERNAME, demoEstimate, demoInverter, demoReadings, demoSales, demoSolarHistory, type DemoState
} from "./fixtures";

/** A separate offline installation. No request, credential, or command can reach a server. */
export class DemoApiClient extends ApiClient {
  private readonly state: DemoState;
  private active = true;
  private nextRuleId = 3;
  private nextRuleRevision = 3;
  private readonly commandReceipts = new Map<string, SocketCommandReceipt>();

  constructor(private readonly clock: () => Date = () => new Date()) {
    super({ baseUrl: DEMO_API_BASE_URL, transport: async () => { throw new Error(t("Demo transport is disabled.")); } });
    this.state = createDemoState(clock());
  }

  override setBaseUrl(_baseUrl: string): void {
    throw new Error(t("Leave demo mode before connecting to a server."));
  }

  override setToken(_token?: string | null): void {
    // Demo never accepts or forwards a real bearer token.
  }

  override async request<T>(path: string, options: RequestOptions = {}): Promise<T> {
    if (options.signal?.aborted) {
      const error = new Error(t("The request was canceled."));
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
    if (route === "POST /api/auth/login") throw new ApiError(400, t("Demo mode does not require a username or password."));
    if (route === "GET /api/auth/session") return { authenticated: this.active, username: this.active ? DEMO_USERNAME : null };
    if (route === "POST /api/auth/logout") { this.active = false; return; }
    if (!this.active) throw new ApiError(401, t("The demo session has ended."));

    if (route === "GET /api/dashboard" || route === "POST /api/dashboard/refresh") return {
      inverter: demoInverter(now, timeZone), devicesLoaded: true, deviceLastUpdated: now.toISOString(),
      devices: this.state.devices, manualDevices: this.state.devices, rules: this.state.rules, timeZoneId: timeZone
    };
    if (route === "GET /api/solar/estimate") return demoEstimate(now, timeZone);
    if (route === "GET /api/solar/history") {
      const period = options.query?.period ?? "Today";
      if (period !== "Today" && period !== "Week" && period !== "Month") throw new ApiError(400, t("Select a valid generation period."));
      return demoSolarHistory(period, optionalDate(options.query?.date), now, timeZone);
    }
    if (route === "GET /api/sales") {
      const period = options.query?.period ?? "Day";
      if (period !== "Day" && period !== "Month" && period !== "Year" && period !== "Custom") throw new ApiError(400, t("Select a valid sales period."));
      const date = optionalDate(options.query?.date) ?? localDate(now, timeZone);
      return demoSales(period, date, now, timeZone);
    }
    if (route === "GET /api/devices") return { devices: this.state.devices, lastUpdated: now.toISOString() };
    const deviceNameRoute = /^\/api\/devices\/([^/]+)\/name$/.exec(path);
    if (method === "PATCH" && deviceNameRoute) {
      const id = decodeURIComponent(deviceNameRoute[1]!);
      const device = this.state.devices.find(item => item.id === id);
      if (!device) throw new ApiError(404, t("Demo device not found."));
      const { name } = objectBody(options.body);
      if (name !== null && (typeof name !== "string" || name.length > 80 || /[\u0000-\u001f\u007f]/.test(name)))
        throw new ApiError(400, t("Enter a name up to 80 characters without control characters."));
      device.cloudName ??= device.name;
      device.localName = typeof name === "string" && name.trim() ? name.trim() : null;
      device.name = device.localName ?? device.cloudName;
      return device;
    }
    const commandRoute = /^\/api\/v2\/devices\/([^/]+)\/commands(?:\/([^/]+))?$/.exec(path);
    if (commandRoute) {
      const deviceId = decodeURIComponent(commandRoute[1]!);
      const receiptId = commandRoute[2] ? decodeURIComponent(commandRoute[2]) : null;
      if (method === "GET") {
        if (!receiptId) return [];
        const receipt = this.commandReceipts.get(receiptId);
        if (!receipt || receipt.deviceId !== deviceId) throw new ApiError(404, t("Demo device not found."));
        return receipt;
      }
      if (method !== "POST" || receiptId) throw new ApiError(404, t("This operation is not available in the offline demo."));
      const body = objectBody(options.body);
      if (typeof body.commandId !== "string" || !body.commandId.trim()) throw new ApiError(400, t("The command response did not identify this operation."));
      const existing = this.commandReceipts.get(body.commandId);
      if (existing) {
        if (existing.deviceId !== deviceId || existing.isOn !== body.isOn) throw new ApiError(409, t("The command response did not identify this operation."));
        return existing;
      }
      const device = this.state.devices.find(item => item.id === deviceId);
      if (!device) throw new ApiError(404, t("Demo device not found."));
      if (typeof body.isOn !== "boolean") throw new ApiError(400, t("Choose an on/off state."));
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
      const receipt: SocketCommandReceipt = { commandId: body.commandId, deviceId, isOn: body.isOn,
        status: "acknowledged", rejection: null, createdAt: now.toISOString(), completedAt: now.toISOString() };
      this.commandReceipts.set(body.commandId, receipt);
      return receipt;
    }
    if (route === "GET /api/rules") return this.state.rules;
    if (route === "POST /api/rules") {
      const request = this.ruleRequest(options.body);
      const device = this.state.devices.find(item => item.id === request.entityId)!;
      const rule: Rule = { ...request, id: this.nextRuleId++, configurationVersion: this.ruleVersion(), currentState: device.isOn, currentStateChangedAt: now.toISOString(), lastEvaluated: now.toISOString() };
      this.state.rules.push(rule);
      return rule;
    }
    const ruleRoute = /^\/api\/rules\/(\d+)(\/enabled)?$/.exec(path);
    if (ruleRoute) {
      const id = Number(ruleRoute[1]);
      const index = this.state.rules.findIndex(rule => rule.id === id);
      const rule = this.state.rules[index];
      if (!rule) throw new ApiError(404, t("Demo rule not found."));
      if (method === "GET" && !ruleRoute[2]) return rule;
      if (method === "PUT" && !ruleRoute[2]) {
        this.requireRuleVersion(rule, objectBody(options.body).configurationVersion);
        this.state.rules[index] = { ...rule, ...this.ruleRequest(options.body), configurationVersion: this.ruleVersion() };
        return this.state.rules[index];
      }
      if (method === "PATCH" && ruleRoute[2]) {
        const { enabled, configurationVersion } = objectBody(options.body);
        this.requireRuleVersion(rule, configurationVersion);
        if (typeof enabled !== "boolean") throw new ApiError(400, t("Choose an enabled state."));
        rule.enabled = enabled;
        rule.configurationVersion = this.ruleVersion();
        return rule;
      }
      if (method === "DELETE" && !ruleRoute[2]) {
        this.requireRuleVersion(rule, options.ifMatch?.replace(/^"(.*)"$/, "$1"));
        this.state.rules.splice(index, 1);
        return;
      }
    }
    if (route === "GET /api/readings") return demoReadings(historyHours(options.query?.hours), now, timeZone);
    if (route === "GET /api/rule-runs") {
      const cutoff = now.getTime() - historyHours(options.query?.hours) * 3600000;
      const filter = String(options.query?.filter ?? "ALL").toUpperCase();
      return this.state.runs.filter(run => Date.parse(run.timestamp) >= cutoff &&
        (filter === "ON" || filter === "OFF" ? run.action === filter : filter === "CHANGES" ? run.action !== "NO_CHANGE" : true));
    }
    if (route === "GET /api/settings") return this.state.settings;
    if (route === "GET /api/settings/site") return this.state.site;
    if (route === "PUT /api/settings/site") {
      const body = objectBody(options.body);
      const estimate = objectBody(body.solarEstimate);
      const sales = objectBody(body.solarSales);
      for (const key of ["latitude", "longitude", "roof1Kwp", "roof2Kwp", "roof1Tilt", "roof2Tilt", "roof1Azimuth", "roof2Azimuth"])
        if (typeof estimate[key] !== "number" || !Number.isFinite(estimate[key])) throw new ApiError(400, t("Enter valid solar site numbers."));
      if (typeof estimate.locationLabel !== "string" || typeof estimate.timeZoneId !== "string"
        || typeof sales.contractStartDate !== "string" || typeof sales.timeZoneId !== "string" || typeof sales.payNegativePrices !== "boolean")
        throw new ApiError(400, t("Enter valid site and sales settings."));
      if (estimate.deyeSolarPowerIsPvDcConfirmed === true && (!this.state.site.selectedDeviceSn
        || estimate.deyeSolarPowerConfirmedDeviceSn !== this.state.site.selectedDeviceSn))
        throw new ApiError(400, t("Confirm the currently saved selected inverter's PV source."));
      this.state.site = JSON.parse(JSON.stringify({ ...body, selectedDeviceSn: this.state.site.selectedDeviceSn,
        solarEstimate: { ...estimate, deyeSolarPowerConfirmedDeviceSn: estimate.deyeSolarPowerIsPvDcConfirmed === true
          ? this.state.site.selectedDeviceSn : "" } })) as SolarSiteSettings;
      return;
    }
    if (method === "POST" && /^\/api\/settings\/test\/(openmeteo|pse)$/.test(path)) return {
      kind: path.split("/").at(-1), success: true, code: "ok", checkedAt: now.toISOString(),
      message: t("Demo connection check simulated. No external services contacted and no settings saved.")
    };
    if (route === "PUT /api/settings/polling") {
      const value = objectBody(options.body) as unknown as PollingSettings;
      requireNumber(value.intervalSeconds, 5, 300, t("polling interval"));
      this.state.settings.polling = { intervalSeconds: value.intervalSeconds };
      return;
    }
    if (route === "PUT /api/settings/display") {
      const value = objectBody(options.body) as unknown as DisplaySettings;
      requireStrings(value, ["timeZoneId"]);
      try { new Intl.DateTimeFormat("en-GB", { timeZone: value.timeZoneId }); }
      catch { throw new ApiError(400, t("Enter a valid timezone.")); }
      this.state.settings.display = { timeZoneId: value.timeZoneId };
      return;
    }
    throw new ApiError(404, t("This operation is not available in the offline demo."));
  }

  private ruleRequest(body: unknown): RuleRequest {
    const value = objectBody(body) as unknown as RuleRequest;
    requireStrings(value, ["name", "entityId"]);
    if (!value.name.trim() || !this.state.devices.some(device => device.id === value.entityId)) throw new ApiError(400, t("Enter a rule name and choose a demo device."));
    for (const key of ["enabled", "useSeparateSocTurnOffThreshold", "useSolarProductionThreshold"] as const) {
      if (typeof value[key] !== "boolean") throw new ApiError(400, t("Enter valid rule options."));
    }
    requireNumber(value.socTurnOnThreshold, 0, 100, t("SOC threshold"));
    requireNumber(value.socTurnOffThreshold, 0, 100, t("SOC threshold"));
    requireNumber(value.minAverageSolarProductionWatts, 0, 30000, t("solar threshold"));
    requireNumber(value.cooldownMinutes, 1, 240, "cooldown");
    requireNumber(value.intervalSeconds, 10, 3600, t("rule interval"));
    for (const time of [value.activeFrom, value.activeTo]) {
      if (time !== null && (typeof time !== "string" || !/^([01]\d|2[0-3]):[0-5]\d$/.test(time))) throw new ApiError(400, t("Use HH:mm for the time window."));
    }
    if (Boolean(value.activeFrom) !== Boolean(value.activeTo)) throw new ApiError(400, t("Set both time-window values or leave both empty."));
    if (value.useSeparateSocTurnOffThreshold && value.socTurnOffThreshold > value.socTurnOnThreshold) throw new ApiError(400, t("Turn OFF SOC cannot exceed turn ON SOC."));
    // Store only known configuration fields, not caller-provided state or IDs.
    return {
      name: value.name.trim(), entityId: value.entityId, sourceInverterId: value.sourceInverterId, enabled: value.enabled,
      socTurnOnThreshold: value.socTurnOnThreshold, useSeparateSocTurnOffThreshold: value.useSeparateSocTurnOffThreshold,
      socTurnOffThreshold: value.socTurnOffThreshold, useSolarProductionThreshold: value.useSolarProductionThreshold,
      minAverageSolarProductionWatts: value.minAverageSolarProductionWatts, cooldownMinutes: value.cooldownMinutes,
      intervalSeconds: value.intervalSeconds, activeFrom: value.activeFrom, activeTo: value.activeTo
    };
  }

  private ruleVersion(): string { return (this.nextRuleRevision++).toString(16).padStart(64, "0"); }

  private requireRuleVersion(rule: Rule, supplied: unknown): void {
    if (typeof supplied !== "string" || !/^[a-fA-F0-9]{64}$/.test(supplied))
      throw new ApiError(428, t("Unable to save rule."));
    if (supplied !== rule.configurationVersion) throw new ApiError(409, t("Unable to save rule."));
  }
}

function objectBody(value: unknown): Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) throw new ApiError(400, t("Enter valid demo settings."));
  return value as Record<string, unknown>;
}

function requireStrings(value: object, keys: string[]): void {
  for (const key of keys) if (typeof (value as Record<string, unknown>)[key] !== "string") throw new ApiError(400, t("Enter valid demo values."));
}

function requireNumber(value: unknown, min: number, max: number, name: string): void {
  if (typeof value !== "number" || !Number.isFinite(value) || value < min || value > max) throw new ApiError(400, t("Enter a valid {0}.", name));
}

function historyHours(value: unknown): number {
  const hours = Number(value ?? 6);
  return Number.isFinite(hours) ? Math.min(168, Math.max(1, hours)) : 6;
}

function optionalDate(value: unknown): string | undefined {
  if (value === undefined || value === null) return undefined;
  if (typeof value !== "string" || !/^\d{4}-\d{2}-\d{2}$/.test(value)) throw new ApiError(400, t("Select a valid date."));
  const date = new Date(`${value}T12:00:00Z`);
  if (!Number.isFinite(date.getTime()) || date.toISOString().slice(0, 10) !== value) throw new ApiError(400, t("Select a valid date."));
  return value;
}

function localDate(now: Date, timeZone: string): string {
  const parts = new Intl.DateTimeFormat("en-GB", { timeZone, year: "numeric", month: "2-digit", day: "2-digit" }).formatToParts(now);
  return ["year", "month", "day"].map(name => parts.find(part => part.type === name)?.value).join("-");
}
