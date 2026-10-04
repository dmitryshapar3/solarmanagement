import { translate as t } from "../i18n";
export type AuthResponse = {
  token: string;
  expiresAt: string;
  username: string;
};

export type AuthOptions = { registrationEnabled: boolean; emailEnabled: boolean; phoneEnabled: boolean; googleEnabled: boolean };
export type VerificationChannel = "email" | "phone";
export type VerificationPurpose = "register" | "login" | "link";
export type VerificationResponse = { verificationId: string; expiresAt: string; retryAfterSeconds: number };

export type SessionResponse = {
  authenticated: boolean;
  username: string | null;
};

export type InverterData = {
  inverterId: string | null;
  batterySocValid: boolean;
  batteryPowerValid: boolean;
  batteryTemperatureValid: boolean;
  batteryVoltageValid: boolean;
  batteryCurrentValid: boolean;
  loadPowerValid: boolean;
  gridPowerValid: boolean;
  solarPowerValid: boolean;
  batterySoc: number;
  batteryTemperature: number;
  batteryVoltage: number;
  batteryPower: number;
  batteryCurrent: number;
  solarProduction: number;
  gridConsumption: number;
  loadPower: number;
  timestamp: string;
  dataSource: string;
  solarObservedAt: string | null;
  gridObservedAt: string | null;
  solarDeviceSn: string | null;
  gridDeviceSn: string | null;
};

export type Device = {
  id: string;
  name: string;
  category: string | null;
  online: boolean;
  isOn: boolean;
  stateKnown: boolean;
  currentPowerW: number | null;
  cloudName?: string | null;
  localName?: string | null;
};

export type IntegrationKind = "openmeteo" | "pse";
export type IntegrationTestRequest = {
  solarEstimate?: { latitude: number; longitude: number };
};
export type IntegrationTestResult = {
  kind: IntegrationKind;
  success: boolean;
  code: "ok" | "configuration" | "authentication" | "unavailable" | "timeout" | "busy";
  message: string;
  checkedAt: string;
};

export type SolarSiteSettings = {
  selectedDeviceSn: string;
  solarEstimate: {
    latitude: number; longitude: number; locationLabel: string; timeZoneId: string;
    roof1Kwp: number; roof2Kwp: number; roof1Tilt: number; roof2Tilt: number;
    roof1Azimuth: number; roof2Azimuth: number;
    deyeSolarPowerIsPvDcConfirmed: boolean; deyeSolarPowerConfirmedDeviceSn: string;
  };
  solarSales: { contractStartDate: string; timeZoneId: string; payNegativePrices: boolean };
};

export type DeviceList = {
  devices: Device[];
  lastUpdated: string | null;
};

export type Rule = {
  configurationVersion: string;
  id: number;
  name: string;
  entityId: string;
  sourceInverterId: string | null;
  enabled: boolean;
  socTurnOnThreshold: number;
  useSeparateSocTurnOffThreshold: boolean;
  socTurnOffThreshold: number;
  useSolarProductionThreshold: boolean;
  minAverageSolarProductionWatts: number;
  cooldownMinutes: number;
  intervalSeconds: number;
  activeFrom: string | null;
  activeTo: string | null;
  currentState: boolean;
  currentStateChangedAt: string | null;
  lastEvaluated: string | null;
};

export type RuleRequest = Omit<
  Rule,
  "id" | "currentState" | "currentStateChangedAt" | "lastEvaluated" | "configurationVersion"
> & { configurationVersion?: string };

export type RuleUpdateRequest = RuleRequest & { configurationVersion: string };

export type Dashboard = {
  inverter: InverterData | null;
  devicesLoaded: boolean;
  deviceLastUpdated: string | null;
  devices: Device[];
  manualDevices: Device[];
  rules: Rule[];
  timeZoneId: string;
};

export type Reading = {
  id: number;
  timestamp: string;
  batterySoc: number;
  batteryTemperature: number;
  batteryVoltage: number;
  batteryPower: number;
  batteryCurrent: number;
  solarProduction: number;
  gridConsumption: number;
  loadPower: number;
  dataSource: string;
};

export type RuleRunLog = {
  id: number;
  timestamp: string;
  ruleName: string;
  action: string;
  conditionKey: string;
  reason: string;
  batterySoc: number;
  solarProduction: number;
  batteryPower: number;
};

export type PollingSettings = {
  intervalSeconds: number;
};

export type DisplaySettings = {
  timeZoneId: string;
};

export type Settings = {
  polling: PollingSettings;
  display: DisplaySettings;
};

export type SolarPowerBasis = 0 | 1 | 2;
export type SolarHistoryPeriod = "Today" | "Week" | "Month";
export type SolarPowerEstimate = {
  timestamp: string;
  calculatedAt: string;
  basis: SolarPowerBasis;
  centralKw: number;
  lowerKw: number;
  upperKw: number;
  totalKwp: number;
  weatherMissing: boolean;
  observation: {
    timestamp: string;
    kind: 0 | 1;
    retrievedAt: string | null;
    weatherTimestamp: string | null;
  };
};

export type SolarEstimateState = {
  estimate: SolarPowerEstimate | null;
  comparisonEstimate: SolarPowerEstimate | null;
  comparison: {
    status: 0 | 1 | 2 | 3;
    actual: { timestamp: string; powerKw: number; basis: SolarPowerBasis } | null;
    deviationKw: number | null;
    deviationPercent: number | null;
    reason: string | null;
  };
  refreshFailed: boolean;
  lastSuccessAt: string | null;
  error: string | null;
};

export type SolarHistoryPoint = {
  timestamp: string;
  possible: { lowerKw: number; upperKw: number } | null;
  actualKw: number | null;
};

export type SolarHistoryResult = {
  start: string;
  end: string;
  timeZoneId: string;
  points: SolarHistoryPoint[];
  weatherError: string | null;
  actualError: string | null;
  selectedDate: string;
  today: string;
};

export type ExportSalesPeriod = "Day" | "Month" | "Year" | "Custom";
export type ExportSaleProgress = {
  start: string;
  observedThrough: string | null;
  exportKwh: number | null;
  creditedExportKwh: number | null;
  energyValuePln: number | null;
  estimatedDepositPln: number | null;
  observedSeconds: number;
};

export type ExportSaleBucket = {
  start: string;
  end: string;
  exportKwh: number | null;
  creditedExportKwh: number | null;
  energyValuePln: number | null;
  estimatedDepositPln: number | null;
  expectedHours: number;
  observedHours: number;
  valuedHours: number;
};

export type ExportSalesResult = {
  request: { period: 0 | 1 | 2 | 3; date: string; from: string | null; through: string | null };
  today: string;
  contractStartDate: string;
  timeZoneId: string;
  start: string;
  end: string;
  buckets: ExportSaleBucket[];
  exportKwh: number | null;
  creditedExportKwh: number | null;
  energyValuePln: number | null;
  estimatedDepositPln: number | null;
  expectedHours: number;
  observedHours: number;
  valuedHours: number;
  dataError: string | null;
  priceError: string | null;
  currentHour: ExportSaleProgress | null;
  updatedAt: string | null;
  isPartial: boolean;
};
