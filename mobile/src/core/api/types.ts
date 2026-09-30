export type AuthResponse = {
  token: string;
  expiresAt: string;
  username: string;
};

export type SessionResponse = {
  authenticated: boolean;
  username: string | null;
};

export type InverterData = {
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
};

export type Device = {
  id: string;
  name: string;
  category: string | null;
  online: boolean;
  isOn: boolean;
  currentPowerW: number | null;
};

export type DeviceList = {
  devices: Device[];
  lastUpdated: string | null;
};

export type Rule = {
  id: number;
  name: string;
  entityId: string;
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
  "id" | "currentState" | "currentStateChangedAt" | "lastEvaluated"
>;

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

export type DeyeCloudSettings = {
  baseUrl: string;
  appId: string;
  appSecret: string;
  email: string;
  password: string;
  stationId: number;
  deviceSn: string;
};

export type ShellySettings = {
  serverUri: string;
  authKey: string;
  deviceId: string;
  requestIntervalMilliseconds: number;
};

export type PollingSettings = {
  intervalSeconds: number;
};

export type DisplaySettings = {
  timeZoneId: string;
};

export type Settings = {
  deyeCloud: DeyeCloudSettings;
  shelly: ShellySettings;
  polling: PollingSettings;
  display: DisplaySettings;
};

export type DeyeStation = {
  id: number;
  name: string;
  address: string | null;
};

export type DeyeDevice = {
  serialNumber: string;
  deviceType: string;
  deviceId: number;
  stationId: number;
};

export type SocketStateResponse = {
  entityId: string;
  isOn: boolean;
  device: Device | null;
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
