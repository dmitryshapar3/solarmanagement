import { translate as t } from "../../core/i18n";
import { normalizeRuleDraft, validateRuleDraft } from "../rules/RuleDraftPolicy";
import { ApiClient, ApiError, type RequestOptions } from "../../core/api/ApiClient";
import type { DisplaySettings, PollingSettings, Rule, RuleRequest, SolarSiteSettings } from "../../core/api/types";
import {
  createDemoState, DEMO_API_BASE_URL, DEMO_USERNAME, demoEstimate, demoInverter, demoReadings, demoSales, demoSolarHistory, demoProduction, type DemoState
} from "./fixtures";

/** A separate offline installation. No request, credential, or command can reach a server. */
export class DemoApiClient extends ApiClient {
  private readonly state: DemoState;
  private active = true;
  private nextRuleId = 3;
  private nextRuleRevision = 3;
  private installationRevision = 1;

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

    if (route === "GET /api/auth/options") return { registrationEnabled: false, emailEnabled: false, phoneEnabled: false, googleEnabled: false };
    if (route === "GET /api/auth/identities") return { email: null, phone: null, googleLinked: false, appleLinked: false, hasPassword: false };
    if (route === "GET /api/auth/security/permissions") return { role: "Owner", permissions: ["Read", "ManageRules", "ManageSettings", "ManageIntegrations"] };
    if (route === "GET /api/account/profile") return { displayName: "Alex", verifiedEmail: null, verifiedPhone: null };
    if (route === "GET /api/account/preferences") return { displayTimeZoneId: this.state.settings.display.timeZoneId };
    if (route === "PUT /api/account/preferences") { this.dispatch("/api/settings/display", { method: "PUT", body: { timeZoneId: objectBody(options.body).displayTimeZoneId } }); return { displayTimeZoneId: this.state.settings.display.timeZoneId }; }
    if (route === "GET /api/account/sessions") return { sessions: [] };
    if (route === "GET /api/v2/integration-providers") return { providers: [], revision: "offline-sample", providerKinds: {} };
    if (route === "GET /api/v2/integrations") return [];
    if (route === "GET /api/v2/integration-socket-sources") return [{ id: "766ce5fb-f18b-4438-8718-d837397a7c78", name: "Demo inverter", isDefault: true }];
    if (route === "GET /api/v2/integrations/status") return { services: [], forecastRetrievedAt: now.toISOString(), latestStoredPriceAt: now.toISOString(), missingPriceHours: 0 };
    if (route === "GET /api/settings/installation") return this.installation();
    if (route === "PUT /api/settings/installation") {
      const body = objectBody(options.body);
      if (body.expectedVersion !== this.installation().version) throw new ApiError(409, t("Settings changed. Pull down to load the latest version."));
      const beforeSite = JSON.parse(JSON.stringify(this.state.site)) as SolarSiteSettings;
      const beforeSettings = JSON.parse(JSON.stringify(this.state.settings));
      try {
        if (body.primaryInverterId !== this.installation().primaryInverterId) throw new ApiError(400, t("Select an available source inverter."));
        this.dispatch("/api/settings/site", { method: "PUT", body: body.site });
        this.dispatch("/api/settings/polling", { method: "PUT", body: body.polling });
        this.dispatch("/api/settings/display", { method: "PUT", body: body.display });
      } catch (error) { this.state.site = beforeSite; this.state.settings = beforeSettings; throw error; }
      ++this.installationRevision;
      return this.installation();
    }

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
    if (route === "GET /api/solar/production") {
      const period = options.query?.period ?? "Today";
      if (period !== "Today" && period !== "Week" && period !== "Month") throw new ApiError(400, t("Select a valid generation period."));
      return demoProduction(period, optionalDate(options.query?.date), now, timeZone);
    }
    if (route === "GET /api/activity") {
      const from = typeof options.query?.from === "string" ? options.query.from : new Date(now.getTime() - 168 * 3600000).toISOString();
      const through = typeof options.query?.to === "string" ? options.query.to : now.toISOString();
      const items = this.state.runs.filter(run => run.timestamp >= from && run.timestamp <= through && (!options.query?.changesOnly || run.action !== "NO_CHANGE"))
        .map(run => ({ id: run.id, start: run.timestamp, end: run.timestamp, kind: run.action === "NO_CHANGE" ? "rule.checked" : "rule.switched", ruleId: this.state.rules.find(rule => rule.name === run.ruleName)?.id ?? null,
          ruleName: run.ruleName, deviceId: null, reasonCode: "sample_data", state: run.action === "ON" ? true : run.action === "OFF" ? false : null, checkCount: 1,
          socMin: run.batterySoc, socMax: run.batterySoc, solarMinWatts: run.solarProduction, solarMaxWatts: run.solarProduction, actorUserId: null, client: "demo" }))
        .filter(item => !options.query?.ruleId || item.ruleId === Number(options.query.ruleId));
      return { start: from, end: through, items, nextCursor: null, summary: { switches: items.filter(item => item.kind === "rule.switched").length, confirmedCommands: 0, onSeconds: 4 * 3600, knownSeconds: 24 * 3600, expectedSeconds: 24 * 3600, partial: false } };
    }
    const checks = /^\/api\/activity\/groups\/(\d+)\/checks$/.exec(path);
    if (method === "GET" && checks) { const run = this.state.runs.find(run => run.id === Number(checks[1])); if (!run) throw new ApiError(404, t("Sample activity not found.")); return { items: [{ id: run.id, occurredAt: run.timestamp, kind: "rule.checked", reasonCode: "sample_data", state: null, batterySoc: run.batterySoc, solarWatts: run.solarProduction, configurationVersion: null, generation: null }], nextCursor: null }; }
    const evaluation = /^\/api\/rules\/(\d+)\/evaluation$/.exec(path);
    if (method === "GET" && evaluation) {
      const rule = this.state.rules.find(item => item.id === Number(evaluation[1]));
      if (!rule) throw new ApiError(404, t("Demo rule not found."));
      const current = demoInverter(now,timeZone);
      return { ruleId: rule.id, configurationVersion: rule.configurationVersion, checkedAt: rule.lastEvaluated,
        nextCheckAt: new Date(now.getTime()+rule.intervalSeconds*1000).toISOString(), decision: "sample_data", state: rule.enabled ? "enabled" : "disabled", freshness: "current", sourceInverterId: current.inverterId,
        conditions: [{ kind: "battery_soc", observed: current.batterySoc, threshold: rule.socTurnOnThreshold, status: current.batterySoc >= rule.socTurnOnThreshold ? "passed" : "blocked", reasonCode: "sample_data" },
          ...(rule.useSolarProductionThreshold ? [{ kind: "solar_average", observed: current.solarProduction, threshold: rule.minAverageSolarProductionWatts, status: current.solarProduction >= rule.minAverageSolarProductionWatts ? "passed" : "blocked", reasonCode: "sample_data" }] : [])] };
    }
    const deviceHistory = /^\/api\/v2\/devices\/([^/]+)\/history$/.exec(path);
    const deviceDetails = /^\/api\/v2\/devices\/([^/]+)\/details$/.exec(path);
    if (method === "GET" && deviceDetails) {
      const device = this.state.devices.find(item => item.id === decodeURIComponent(deviceDetails[1]!));
      if (!device) throw new ApiError(404, t("Demo device not found."));
      return { id: device.id, name: device.name, device, providerId: "sample.smart-plug", providerDisplayName: "Sample smart plug", model: null, instanceId: null,
        sourceInverterId: null, phaseCount: 1, controllingRules: this.state.rules.filter(rule => rule.entityId === device.id),
        lastConfirmedSwitch: null, addedAt: null, canSwitch: false, supportsHistory: true };
    }
    if (method === "GET" && deviceHistory) {
      const device = this.state.devices.find(item => item.id === decodeURIComponent(deviceHistory[1]!));
      if (!device) throw new ApiError(404, t("Demo device not found."));
      const start = new Date(now.getTime()-24*3600000).toISOString();
      const intervals = Array.from({length:24},(_,i)=>({from:new Date(now.getTime()-(24-i)*3600000).toISOString(),to:new Date(now.getTime()-(23-i)*3600000).toISOString(),isOn:i%4===0,evidence:"synthetic_sample"}));
      return {start,end:now.toISOString(),intervals,onSeconds:6*3600,knownSeconds:24*3600,partial:false};
    }
    if (route === "GET /api/readings" && (options.query?.view === "details" || options.query?.aggregate === "5m")) {
      const aggregate = options.query?.aggregate === "5m" ? "5m" : "raw"; const step = aggregate === "5m" ? 5 : 15;
      const count = Math.ceil(historyHours(options.query?.hours) * 60 / step); const page = Number(options.query?.cursor ?? 0);
      if (!Number.isInteger(page) || page < 0) throw new ApiError(400, t("Select a valid history cursor."));
      const rows = Array.from({ length: Math.max(0, Math.min(500, count - page)) }, (_, index) => { const at = new Date(now.getTime() - (page + index) * step * 60000); return { ...demoInverter(at, timeZone), id: page + index + 1 }; });
      return { start:new Date(now.getTime()-historyHours(options.query?.hours)*3600000).toISOString(),end:now.toISOString(),aggregate,
        items:rows.map(row=>({...row,inverterId:"766ce5fb-f18b-4438-8718-d837397a7c78",configurationRevision:1,runtimeGeneration:1,solarObservedAt:row.timestamp})),gaps:[],nextCursor:page+rows.length<count?String(page+rows.length):null,partial:false };
    }
    if (route === "GET /api/sales" || route === "POST /api/sales/prices/recheck") {
      if (method === "POST") options = { ...options, query: objectBody(options.body) as RequestOptions["query"] };
      const period = options.query?.period ?? "Day";
      if (period !== "Day" && period !== "Month" && period !== "Year" && period !== "Custom") throw new ApiError(400, t("Select a valid sales period."));
      const date = optionalDate(options.query?.date) ?? localDate(now, timeZone);
      const from = optionalDate(options.query?.from); const through = optionalDate(options.query?.through);
      if (period === "Custom" && (!from || !through || from > through || (Date.parse(through) - Date.parse(from)) / 86400000 + 1 > 366 || through > localDate(now, timeZone))) throw new ApiError(400, t("Choose a range of at most 366 days, ending today or earlier."));
      return demoSales(period, date, now, timeZone, from && through ? { from, through } : undefined);
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
        throw new ApiError(404, t("Demo device not found."));
      }
      if (method !== "POST" || receiptId) throw new ApiError(404, t("This operation is not available in the offline demo."));
      throw new ApiError(403, t("Switching is turned off in the demo."));
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
        const request = this.ruleRequest(options.body);
        const targetChanged = request.entityId !== rule.entityId;
        const target = this.state.devices.find(device => device.id === request.entityId)!;
        this.state.rules[index] = { ...rule, ...request, configurationVersion: this.ruleVersion(),
          ...(targetChanged ? { currentState: target.isOn, currentStateChangedAt: now.toISOString(), lastEvaluated: null } : {}) };
        return this.state.rules[index];
      }
      if (method === "PATCH" && ruleRoute[2]) {
        const { enabled, configurationVersion } = objectBody(options.body);
        this.requireRuleVersion(rule, configurationVersion);
        if (typeof enabled !== "boolean") throw new ApiError(400, t("Choose an enabled state."));
        rule.enabled = enabled;
        if (enabled) { rule.pauseReason = null; rule.pausedAt = null; rule.pausedByUserId = null; }
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

  private installation() {
    return { site: this.state.site, polling: this.state.settings.polling, display: this.state.settings.display,
      primaryInverterId: "766ce5fb-f18b-4438-8718-d837397a7c78", inverters: [{ id: "766ce5fb-f18b-4438-8718-d837397a7c78", name: "Demo inverter", isDefault: true }],
      version: this.installationRevision.toString(16).padStart(64, "0"), integrationVersions: {} };
  }

  private ruleRequest(body: unknown): RuleRequest {
    const value = objectBody(body) as unknown as RuleRequest;
    requireStrings(value, ["name", "entityId"]);
    if (!value.name.trim() || !this.state.devices.some(device => device.id === value.entityId)) throw new ApiError(400, t("Enter a rule name and choose a demo device."));
    for (const key of ["enabled", "useSeparateSocTurnOffThreshold", "useSolarProductionThreshold"] as const) {
      if (typeof value[key] !== "boolean") throw new ApiError(400, t("Enter valid rule options."));
    }
    for (const key of ["socTurnOnThreshold", "socTurnOffThreshold", "minAverageSolarProductionWatts", "cooldownMinutes", "intervalSeconds"] as const) {
      if (!Number.isInteger(value[key])) throw new ApiError(400, t("Enter valid rule options."));
    }
    for (const time of [value.activeFrom, value.activeTo]) {
      if (time !== null && typeof time !== "string") throw new ApiError(400, t("Use HH:mm for the time window."));
    }
    const normalized = normalizeRuleDraft(value);
    const error = validateRuleDraft(normalized);
    if (error) throw new ApiError(400, t(error.message));
    // The shared draft projection excludes runtime state and IDs; the token is a request precondition.
    const { configurationVersion: _version, ...configuration } = normalized;
    return configuration;
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
