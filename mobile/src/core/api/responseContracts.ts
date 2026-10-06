// Runtime contracts protect stateful views and secure session persistence from malformed
// JSON. Transport timing/session rules remain in ApiClient; feature policy remains in callers.
type Check = (value: unknown) => boolean;
const str: Check = v => typeof v === "string";
const num: Check = v => typeof v === "number" && Number.isFinite(v);
const bool: Check = v => typeof v === "boolean";
const date: Check = v => str(v) && Number.isFinite(Date.parse(v as string));
const nullable = (check: Check): Check => v => v === null || check(v);
const optional = (check: Check): Check => v => v === undefined || check(v);
const array = (check: Check): Check => v => Array.isArray(v) && v.length <= 100_000 && v.every(check);
const object = (fields: Record<string, Check>): Check => v => typeof v === "object" && v !== null && !Array.isArray(v)
  && Object.entries(fields).every(([key, check]) => check((v as Record<string, unknown>)[key]));
const record = (check: Check): Check => v => typeof v === "object" && v !== null && !Array.isArray(v) && Object.values(v).every(check);
const oneOf = (...values: unknown[]): Check => v => values.includes(v);
const value: Check = v => v === null || str(v) || bool(v) || num(v);
const strings = (keys: string[]) => Object.fromEntries(keys.map(k => [k, str]));
const numbers = (keys: string[]) => Object.fromEntries(keys.map(k => [k, num]));
const energy = numbers(["batterySoc", "batteryTemperature", "batteryVoltage", "batteryPower", "batteryCurrent", "solarProduction", "gridConsumption", "loadPower"]);
const measurementValidity = Object.fromEntries(["batterySocValid", "batteryPowerValid", "batteryTemperatureValid", "batteryVoltageValid", "batteryCurrentValid", "loadPowerValid", "gridPowerValid", "solarPowerValid"].map(k => [k, bool]));
const device = object({ id: str, name: str, category: nullable(str), online: bool, isOn: bool,
  currentPowerW: nullable(num), stateKnown: bool, cloudName: optional(nullable(str)), localName: optional(nullable(str)) });
const rule = object({ id: num, name: str, entityId: str, enabled: bool, currentState: bool,
  ...numbers(["socTurnOnThreshold", "socTurnOffThreshold", "minAverageSolarProductionWatts", "cooldownMinutes", "intervalSeconds"]),
  useSeparateSocTurnOffThreshold: bool, useSolarProductionThreshold: bool, activeFrom: nullable(str), activeTo: nullable(str),
  lastEvaluated: nullable(date), currentStateChangedAt: nullable(date), sourceInverterId: nullable(str),
  configurationVersion: v => typeof v === "string" && /^[a-fA-F0-9]{64}$/.test(v),
  pauseReason: optional(nullable(str)), pausedAt: optional(nullable(date)), pausedByUserId: optional(nullable(str)), pausedByCommandId: optional(nullable(str)) });
const inverter = object({ ...energy, timestamp: date, dataSource: str,
  inverterId: nullable(str), solarObservedAt: nullable(date), gridObservedAt: nullable(date), solarDeviceSn: nullable(str), gridDeviceSn: nullable(str),
  ...measurementValidity });
const dashboard = object({ inverter: nullable(inverter), devicesLoaded: bool, deviceLastUpdated: nullable(date), devices: array(device), manualDevices: array(device), rules: array(rule), timeZoneId: str });
const auth = object({ token: v => typeof v === "string" && v.length > 0 && v.length <= 4096, username: str, expiresAt: date });
const verification = object({ verificationId: str, expiresAt: date, retryAfterSeconds: num });
const settings = object({ polling: object({ intervalSeconds: num }), display: object({ timeZoneId: str }) });
const site = object({ solarEstimate: object({ ...numbers(["latitude", "longitude", "roof1Kwp", "roof2Kwp", "roof1Tilt", "roof2Tilt", "roof1Azimuth", "roof2Azimuth"]),
  locationLabel: str, timeZoneId: str, deyeSolarPowerIsPvDcConfirmed: bool, deyeSolarPowerConfirmedDeviceSn: str }),
  solarSales: object({ contractStartDate: str, timeZoneId: str, payNegativePrices: bool }), selectedDeviceSn: str });
const instance = object({ ...strings(["id", "providerId", "name", "status", "packageVersion", "packageDigest", "descriptorDigest"]), revision: num, generation: num });
const field = object({ key: str, kind: str, label: str, required: bool, secret: bool, defaultValue: optional(value), minimum: optional(nullable(num)), maximum: optional(nullable(num)),
  options: optional(nullable(array(object({ value: str, label: str })))) });
const provider = object({ ...strings(["providerId", "packageVersion", "packageDigest", "descriptorDigest", "displayName"]), uiContractVersion: num, configurationVersion: num,
  requiredUiFeatures: array(str), fields: array(field), actions: array(str) });
const configuration = object({ instance, values: record(value), secretPresent: record(bool) });
const binding = object({ ...strings(["id", "instanceId", "kind", "name", "remoteId"]), isDefault: bool, channel: optional(nullable(str)), sourceInverterId: optional(nullable(str)), phaseCount: optional(oneOf(1, 3)) });
const source = object({ id: str, name: str, isDefault: bool });
const identities = object({ email: nullable(str), phone: nullable(str), googleLinked: bool,
  appleLinked: optional(bool), hasPassword: optional(bool) });
const command = object({ commandId: str, deviceId: str, isOn: bool, status: oneOf("pending", "acknowledged", "rejected", "uncertain", "uncertain_closed"), rejection: nullable(str), createdAt: date, completedAt: nullable(date) });
const oauthStatus = object({ flowId: str, status: str, expiresAt: date, values: record(value), secretPresent: record(bool), code: optional(nullable(str)) });
const forecast = object({ timestamp: date, calculatedAt: date, basis: oneOf(0, 1, 2), ...numbers(["centralKw", "lowerKw", "upperKw", "totalKwp"]), weatherMissing: bool,
  observation: object({ timestamp: date, kind: oneOf(0, 1), retrievedAt: nullable(date), weatherTimestamp: nullable(date) }) });
const solarState = object({ estimate: nullable(forecast), comparisonEstimate: nullable(forecast), comparison: object({ status: oneOf(0, 1, 2, 3),
  actual: nullable(object({ timestamp: date, powerKw: num, basis: oneOf(0, 1, 2) })), deviationKw: nullable(num), deviationPercent: nullable(num), reason: nullable(str) }),
  refreshFailed: bool, lastSuccessAt: nullable(date), error: nullable(str) });
const salesValues = Object.fromEntries(["exportKwh", "creditedExportKwh", "energyValuePln", "estimatedDepositPln"].map(k => [k, nullable(num)]));
const sales = object({ request: object({ period: oneOf(0, 1, 2, 3), date: str, from: nullable(str), through: nullable(str) }),
  ...strings(["today", "contractStartDate", "timeZoneId", "start", "end"]), ...salesValues, ...numbers(["expectedHours", "observedHours", "valuedHours"]),
  buckets: array(object({ start: date, end: date, ...salesValues, ...numbers(["expectedHours", "observedHours", "valuedHours"]) })), dataError: nullable(str), priceError: nullable(str),
  currentHour: nullable(object({ start: date, observedThrough: nullable(date), ...salesValues, observedSeconds: num })), updatedAt: nullable(date), isPartial: bool,
  hours: optional(array(object({ start: date, exportKwh: nullable(num), importKwh: nullable(num), creditedExportKwh: nullable(num), energyValuePln: nullable(num), observedSeconds: num, averagePricePlnPerKwh: nullable(num), marketAveragePricePlnPerKwh: optional(nullable(num)) }))), missingPriceHours: optional(array(date)) });
const productionHour = object({ timestamp: date, actualKw: nullable(num), observedEnergyKwh: nullable(num), coveredSeconds: num, expectedSeconds: num,
  expectedKw: nullable(num), lowerKw: nullable(num), upperKw: nullable(num), partial: bool });
const production = object({ start: date, end: date, timeZoneId: str, date: str, today: str, hours: array(productionHour),
  days: array(object({ date: str, observedEnergyKwh: nullable(num), coveredSeconds: num, expectedSeconds: num,
    expectedEnergyKwh: nullable(num), lowerEnergyKwh: nullable(num), upperEnergyKwh: nullable(num), partial: bool })),
  observedEnergyKwh: nullable(num), completedEnergyKwh: nullable(num), coveredSeconds: num, expectedSeconds: num, expectedEnergyKwh: nullable(num),
  bestHour: nullable(productionHour), currentHour: nullable(productionHour), sunrise: nullable(date), sunset: nullable(date), nextSunrise: nullable(date),
  forecastRetrievedAt: nullable(date), weatherError: nullable(str), actualError: nullable(str), partial: bool });
const activity = object({ start: date, end: date, nextCursor: nullable(str), items: array(object({ id: num, start: date, end: date,
  kind: str, ruleId: nullable(num), ruleName: nullable(str), deviceId: nullable(str), reasonCode: nullable(str), state: nullable(bool), checkCount: num,
  socMin: nullable(num), socMax: nullable(num), solarMinWatts: nullable(num), solarMaxWatts: nullable(num), actorUserId: nullable(str), client: nullable(str) })),
  summary: object({ switches: num, confirmedCommands: num, onSeconds: nullable(num), knownSeconds: num, expectedSeconds: num, partial: bool }) });
const detailedReadings = object({ start: date, end: date, aggregate: str, nextCursor: nullable(str), partial: bool,
  gaps: array(object({ from: date, to: date, reasonCode: str })), items: array(object({ id: num, timestamp: date, inverterId: nullable(str),
    configurationRevision: num, runtimeGeneration: num, dataSource: str, solarObservedAt: nullable(date),
    ...Object.fromEntries(Object.keys(energy).map(k => [k, nullable(num)])) })) });

export function validApiResponse(path: string, method: string, payload: unknown, query?: Record<string, unknown>): boolean {
  const check = contract(path, method, query);
  return check === undefined || check(payload);
}
function contract(path: string, method: string, query?: Record<string, unknown>): Check | undefined {
  if (["/api/auth/login", "/api/auth/register", "/api/auth/verification/login", "/api/auth/google/exchange", "/api/auth/code/complete", "/api/auth/apple/exchange"].includes(path)) return auth;
  if (path === "/api/auth/session") return object({ authenticated: bool, username: nullable(str) });
  if (path === "/api/auth/options") return object({ registrationEnabled: bool, emailEnabled: bool, phoneEnabled: bool, googleEnabled: bool, appleEnabled: optional(bool), appleWebEnabled: optional(bool) });
  if (path === "/api/auth/identities") return identities;
  if (path === "/api/auth/verification/start" || path === "/api/auth/security/verification/start" || path === "/api/auth/code/start") return verification;
  if (path === "/api/auth/google/link/start") return object({ authorizationUrl: str, expiresAt: date });
  if (path === "/api/auth/apple/link/start") return object({ flowId: str, rawNonce: str, expiresAt: date, authorizationUrl: nullable(str) });
  if (path === "/api/auth/apple/link/complete") return identities;
  if (path === "/api/auth/security/permissions") return object({ role: nullable(str), permissions: array(oneOf("Read", "ManageRules", "ControlDevices", "ManageSettings", "ManageIntegrations")) });
  if (path === "/api/auth/security/password" || path === "/api/auth/security/revoke-all") return object({ signedOut: oneOf(true) });
  if (path === "/api/auth/security/delete") return object({ deleted: oneOf(true) });
  if (path === "/api/auth/security/export") return object({ exportedAt: date, account: object({ id: str }), memberships: array(object({ installationId: str, role: str })) });
  if (path === "/api/account/language") return object({ language: nullable(str) });
  if (path === "/api/account/profile") return object({ displayName: nullable(str), verifiedEmail: nullable(str), verifiedPhone: nullable(str) });
  if (path === "/api/account/preferences") return object({ displayTimeZoneId: nullable(str) });
  if (path === "/api/account/sessions") return object({ sessions: array(object({ id: str, platform: nullable(str), client: nullable(str), lastSeenAt: nullable(date), createdAt: date, isCurrent: bool })) });
  if (/^\/api\/account\/sessions\/(?:[^/]+\/revoke|revoke-others)$/.test(path)) return object({ revoked: oneOf(true) });
  if (/^\/api\/account\/identities\/[^/]+\/unlink$/.test(path)) return identities;
  if (path === "/api/account/contacts/change/complete") return object({ displayName: nullable(str), verifiedEmail: nullable(str), verifiedPhone: nullable(str) });
  if (path === "/api/account/contacts/change/start") return object({ challengeId: str, expiresAt: date, retryAfterSeconds: num });
  if (path === "/api/account/proof/external/start") return object({ flowId: str, expiresAt: date, rawNonce: nullable(str), authorizationUrl: nullable(str) });
  if (path === "/api/account/proof/external/complete") return object({ externalProofId: str, expiresAt: date });
  if (path === "/api/dashboard" || path === "/api/dashboard/refresh") return dashboard;
  if (path === "/api/devices") return object({ devices: array(device), lastUpdated: nullable(date) });
  if (/^\/api\/devices\/[^/]+\/name$/.test(path)) return device;
  if (path === "/api/rules") return method === "GET" ? array(rule) : rule;
  if (/^\/api\/rules\/\d+(?:\/enabled)?$/.test(path) && method !== "DELETE") return rule;
  if (path === "/api/settings" && method === "GET") return settings;
  if (path === "/api/settings/site" && method === "GET") return site;
  if (path === "/api/settings/installation") return object({ site, polling: object({ intervalSeconds: num }), display: object({ timeZoneId: str }), primaryInverterId: nullable(str), inverters: array(source), version: str,
    integrationVersions: record(object({ expectedRevision: num, packageVersion: str, packageDigest: str, descriptorDigest: str })) });
  if (/^\/api\/settings\/test\/(openmeteo|pse)$/.test(path)) return object({ kind: str, success: bool, code: str, message: str, checkedAt: date });
  if (path === "/api/readings") return query?.view === "details" || query?.aggregate === "5m" ? detailedReadings : array(object({ id: num, timestamp: date, ...energy, ...measurementValidity, dataSource: str }));
  if (path === "/api/rule-runs") return array(object({ id: num, timestamp: date, ...strings(["ruleName", "action", "conditionKey", "reason"]),
    batterySoc: nullable(num), solarProduction: nullable(num), batteryPower: nullable(num) }));
  if (path === "/api/solar/estimate") return solarState;
  if (path === "/api/solar/history") return object({ ...strings(["start", "end", "timeZoneId", "selectedDate", "today"]), weatherError: nullable(str), actualError: nullable(str),
    points: array(object({ timestamp: date, possible: nullable(object({ lowerKw: num, upperKw: num })), actualKw: nullable(num) })) });
  if (path === "/api/sales" || path === "/api/sales/prices/recheck") return sales;
  if (path === "/api/solar/production") return production;
  if (path === "/api/activity") return activity;
  if (/^\/api\/activity\/groups\/\d+\/checks$/.test(path)) return object({ nextCursor: nullable(str), items: array(object({ id: num, occurredAt: date, kind: str, reasonCode: nullable(str), batterySoc: nullable(num), solarWatts: nullable(num), state: nullable(bool), configurationVersion: nullable(str), generation: nullable(num) })) });
  if (/^\/api\/v2\/devices\/[^/]+\/details$/.test(path)) return object({ id: str, name: str, device: nullable(device), providerId: nullable(str), providerDisplayName: optional(nullable(str)), model: nullable(str), instanceId: nullable(str), sourceInverterId: nullable(str), phaseCount: oneOf(1, 3), controllingRules: array(rule), lastConfirmedSwitch: nullable(date), addedAt: nullable(date), canSwitch: bool, supportsHistory: bool });
  if (path === "/api/v2/integrations/status") return object({ services: array(object({ id: str, providerId: str, name: str, kind: str, status: str, enabled: bool, socketCount: num, defaultInverterName: nullable(str), lastReadingAt: nullable(date), lastConfirmedSwitch: nullable(date) })), forecastRetrievedAt: nullable(date), latestStoredPriceAt: nullable(date), missingPriceHours: num });
  if (/^\/api\/v2\/devices\/[^/]+\/history$/.test(path)) return object({ start: date, end: date, onSeconds: nullable(num), knownSeconds: num,
    partial: bool, intervals: array(object({ from: date, to: date, isOn: nullable(bool), evidence: str })) });
  if (/^\/api\/rules\/\d+\/evaluation$/.test(path)) return object({ ruleId: num, configurationVersion: nullable(str), checkedAt: nullable(date), nextCheckAt: nullable(date),
    decision: str, state: str, freshness: str, sourceInverterId: nullable(str), conditions: array(object({ kind: str, observed: nullable(num), threshold: nullable(num), status: str, reasonCode: nullable(str) })) });
  if (path === "/api/v2/integration-providers") return object({ providers: array(provider), revision: str, providerKinds: optional(record(array(oneOf("inverter", "socket")))) });
  if (/^\/api\/v2\/integration-providers\/[^/]+\/versions\/[^/]+\/ui$/.test(path)) return provider;
  if (/^\/api\/v2\/integration-providers\/[^/]+\/versions$/.test(path)) return array(provider);
  if (path === "/api/v2/integrations") return method === "GET" ? array(instance) : instance;
  if (/^\/api\/v2\/integrations\/[^/]+\/configuration$/.test(path)) return configuration;
  if (/^\/api\/v2\/integrations\/[^/]+\/(enable|disable|package)$/.test(path)) return instance;
  if (/^\/api\/v2\/integrations\/[^/]+\/test$/.test(path)) return object({ success: bool, code: str, message: str });
  if (/^\/api\/v2\/integrations\/[^/]+\/discovery$/.test(path)) return object({ devices: array(object({ ...strings(["selectionToken", "name", "kind", "remoteId"]), channel: optional(nullable(str)) })), expiresAt: date });
  if (/^\/api\/v2\/integrations\/[^/]+\/devices$/.test(path)) return array(binding);
  if (/^\/api\/v2\/integrations\/[^/]+\/devices\/(selection|[^/]+\/source)$/.test(path)) return binding;
  if (path === "/api/v2/integration-socket-sources") return array(object({ id: str, name: str, isDefault: bool }));
  if (/^\/api\/v2\/integrations\/[^/]+\/oauth\/start$/.test(path)) return object({ ...strings(["flowId", "authorizationUrl", "returnUri", "returnNonce"]), expiresAt: date });
  if (/^\/api\/v2\/integrations\/[^/]+\/oauth\/[^/]+(?:\/cancel)?$/.test(path)) return oauthStatus;
  if (/^\/api\/v2\/devices\/[^/]+\/commands(?:\/[^/]+(?:\/release)?)?$/.test(path)) return method === "GET" && path.endsWith("/commands") ? array(command) : command;
  return undefined; // Transport is also used by test/demo/custom paths; application responses above have explicit contracts.
}
