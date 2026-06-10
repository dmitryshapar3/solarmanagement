export type AuthResponse = {
  token: string;
  expiresAt: string;
  username: string;
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
