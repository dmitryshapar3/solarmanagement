import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Keyboard, Pressable, StyleSheet, Text, View } from "react-native";
import { LogOut, MapPin, Power, RefreshCcw, Save } from "lucide-react-native";
import {
  AppButton,
  Card,
  EmptyState,
  ErrorBanner,
  Header,
  LoadingState,
  Screen,
  SectionTitle,
  StatusPill,
  TextField
} from "../../core/components";
import {
  Device,
  DeyeCloudSettings,
  DeyeDevice,
  DeyeStation,
  Settings,
  ShellySettings
} from "../../core/api/types";
import type { IntegrationKind, IntegrationTestResult, SolarSiteSettings } from "../../core/api/types";
import { setDisplayTimeZone } from "../../core/format";
import { colors, spacing, typography } from "../../core/theme";
import { useAuth } from "../../application/AuthContext";
import { AccountIdentityCard } from "../auth/AccountIdentityCard";

const deviceTimeZone = (() => {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || null;
  } catch {
    return null;
  }
})();

const siteNumberFields = [
  ["latitude", "Latitude"], ["longitude", "Longitude"],
  ["roof1Kwp", "Array 1 capacity (kWp)"], ["roof1Tilt", "Array 1 tilt (degrees)"], ["roof1Azimuth", "Array 1 compass bearing (degrees)"],
  ["roof2Kwp", "Array 2 capacity (kWp; 0 if unused)"], ["roof2Tilt", "Array 2 tilt (degrees)"], ["roof2Azimuth", "Array 2 compass bearing (degrees)"]
] as const;
type SiteNumberKey = typeof siteNumberFields[number][0];

export function SettingsScreen() {
  const { api, apiBaseUrl, isDemo, updateApiBaseUrl, logout } = useAuth();
  const [baseUrl, setBaseUrl] = useState(apiBaseUrl);
  const [settings, setSettings] = useState<Settings | null>(null);
  const [shellyIntervalText, setShellyIntervalText] = useState("");
  const [pollingIntervalText, setPollingIntervalText] = useState("");
  const [stations, setStations] = useState<DeyeStation[]>([]);
  const [selectedStationId, setSelectedStationId] = useState<number>(0);
  const [deyeDevices, setDeyeDevices] = useState<DeyeDevice[]>([]);
  const [socketDevices, setSocketDevices] = useState<Device[]>([]);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [testResults, setTestResults] = useState<Partial<Record<IntegrationKind, IntegrationTestResult>>>({});
  const actionPending = useRef(false);
  const [site, setSite] = useState<SolarSiteSettings | null>(null);
  const [siteNumbers, setSiteNumbers] = useState<Partial<Record<SiteNumberKey, string>>>({});

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      let next = await api.getSettings();

      // Mirror the web's first-load auto-detect: adopt the device timezone
      // while the stored value is still the "UTC" default.
      if (next.display.timeZoneId === "UTC" && deviceTimeZone && deviceTimeZone !== "UTC") {
        try {
          await api.saveDisplay({ timeZoneId: deviceTimeZone });
          next = { ...next, display: { timeZoneId: deviceTimeZone } };
        } catch {
          // keep UTC if the server rejects the detected timezone
        }
      }

      setSettings(next);
      setShellyIntervalText(String(next.shelly.requestIntervalMilliseconds));
      setPollingIntervalText(String(next.polling.intervalSeconds));
      setSelectedStationId(next.deyeCloud.stationId);
      setDisplayTimeZone(next.display.timeZoneId);
      try {
        const nextSite = await api.getSiteSettings();
        setSite(nextSite);
        setSiteNumbers(Object.fromEntries(siteNumberFields.map(([key]) => [key, String(nextSite.solarEstimate[key])])));
      } catch { setSite(null); }
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Unable to load settings.");
    } finally {
      setLoading(false);
    }
  }, [api]);

  useEffect(() => {
    void load();
  }, [load]);

  const currentSocketEntityId = useMemo(() => {
    if (!settings) {
      return "";
    }

    const rawId = settings.shelly.deviceId;
    if (!rawId) {
      return "";
    }

    return `shelly:${rawId}`;
  }, [settings]);

  function patchDeyeCloud(value: Partial<DeyeCloudSettings>) {
    setSettings((current) => current && { ...current, deyeCloud: { ...current.deyeCloud, ...value } });
  }

  function patchShelly(value: Partial<ShellySettings>) {
    setSettings((current) => current && { ...current, shelly: { ...current.shelly, ...value } });
  }

  async function runBusy(label: string, action: () => Promise<void>) {
    if (actionPending.current) return;
    actionPending.current = true;
    Keyboard.dismiss();
    setBusy(label);
    setError(null);
    try {
      await action();
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Action failed.");
    } finally {
      actionPending.current = false;
      setBusy(null);
    }
  }

  if (loading) {
    return (
      <Screen scroll={false}>
        <LoadingState label="Loading settings..." />
      </Screen>
    );
  }

  if (!settings) {
    return (
      <Screen>
        <Header
          title="Settings"
          subtitle="Account and installation settings"
          action={<AppButton label={isDemo ? "Exit demo" : "Logout"} icon={LogOut} onPress={() => void logout()} variant="secondary" compact />}
        />
        <ErrorBanner message={error ?? "Settings could not be loaded."} />
        <AppButton label="Retry" icon={RefreshCcw} onPress={() => void load()} variant="secondary" />
      </Screen>
    );
  }

  const shellyPayload = (): ShellySettings => {
    const parsed = Number.parseInt(shellyIntervalText, 10);
    if (Number.isNaN(parsed) || parsed < 100 || parsed > 60000) {
      throw new Error("Shelly request interval must be between 100 and 60000 milliseconds.");
    }
    return { ...settings.shelly, requestIntervalMilliseconds: parsed };
  };

  const saveShelly = async (): Promise<void> => {
    const payload = shellyPayload();
    await api.saveShelly(payload);
    setSettings((current) => current && { ...current, shelly: payload });
    setShellyIntervalText(String(payload.requestIntervalMilliseconds));
  };

  const savePolling = async (): Promise<void> => {
    const parsed = Number.parseInt(pollingIntervalText, 10);
    if (Number.isNaN(parsed) || parsed < 5 || parsed > 300) {
      throw new Error("Polling interval must be between 5 and 300 seconds.");
    }

    await api.savePolling({ intervalSeconds: parsed });
    setSettings((current) => current && { ...current, polling: { intervalSeconds: parsed } });
    setPollingIntervalText(String(parsed));
  };

  const saveDisplay = async (): Promise<void> => {
    const timeZoneId = settings.display.timeZoneId.trim();
    await api.saveDisplay({ timeZoneId });
    setSettings((current) => current && { ...current, display: { timeZoneId } });
    setDisplayTimeZone(timeZoneId);
  };

  async function updateSiteSelection() {
    if (!site) return;
    const saved = await api.getSiteSettings();
    setSite(current => current && { ...current, selectedDeviceSn: saved.selectedDeviceSn,
      solarEstimate: { ...current.solarEstimate,
        deyeSolarPowerIsPvDcConfirmed: saved.solarEstimate.deyeSolarPowerIsPvDcConfirmed,
        deyeSolarPowerConfirmedDeviceSn: saved.solarEstimate.deyeSolarPowerConfirmedDeviceSn } });
  }

  async function saveDeye() {
    if (!settings) return;
    await api.saveDeyeCloud(settings.deyeCloud);
    await updateSiteSelection();
  }

  const selectDeyeDevice = async (device: DeyeDevice): Promise<void> => {
    const deyeCloud = await api.selectDeyeDevice({
      stationId: selectedStationId || device.stationId,
      serialNumber: device.serialNumber
    });
    patchDeyeCloud(deyeCloud);
    await updateSiteSelection();
  };

  const fetchStations = async (): Promise<void> => {
    setStations([]);
    setDeyeDevices([]);
    const nextStations = await api.fetchDeyeStations();
    setStations(nextStations);
    if (nextStations.length === 1) {
      setSelectedStationId(nextStations[0]?.id ?? 0);
    }
  };

  const fetchDeyeDevices = async (): Promise<void> => {
    setDeyeDevices([]);
    const fetched = await api.fetchDeyeDevices(selectedStationId);
    setDeyeDevices(fetched);
  };

  const selectSocket = async (entityId: string): Promise<void> => {
    const updated = await api.selectSocketDevice(entityId);
    setSettings((current) => current && {
      ...current,
      shelly: { ...current.shelly, deviceId: updated.shelly.deviceId }
    });
  };

  const fetchSocketDevices = async (): Promise<void> => {
    setSocketDevices([]);
    const result = await api.getDevices(true);
    setSocketDevices(result.devices);
  };

  const testSocket = async (isOn: boolean): Promise<void> => {
    const response = await api.setDeviceState(currentSocketEntityId, isOn);
    setSocketDevices((current) => current.map((device) =>
      device.id.toLowerCase() === response.entityId.toLowerCase()
        ? { ...device, online: true, isOn: response.isOn }
        : device
    ));
  };

  const testIntegration = async (kind: IntegrationKind): Promise<void> => {
    setTestResults(current => ({ ...current, [kind]: undefined }));
    const draft = kind === "deye" ? { deyeCloud: settings.deyeCloud }
      : kind === "shelly" ? { shelly: shellyPayload() }
        : kind === "openmeteo" && site ? { solarEstimate: { latitude: siteNumber("latitude"), longitude: siteNumber("longitude") } } : {};
    const result = await api.testIntegration(kind, draft);
    setTestResults(current => ({ ...current, [kind]: result }));
  };

  function siteNumber(key: SiteNumberKey): number {
    const text = siteNumbers[key]?.trim();
    const value = text ? Number(text.replace(",", ".")) : Number.NaN;
    if (!Number.isFinite(value)) throw new Error("Enter valid numbers for the solar site.");
    return value;
  }

  async function saveSite() {
    if (!site) return;
    const solarEstimate = { ...site.solarEstimate };
    for (const [key] of siteNumberFields) solarEstimate[key] = siteNumber(key);
    const next = { ...site, solarEstimate };
    await api.saveSiteSettings(next);
    setSite(next);
  }

  const testAction = (kind: IntegrationKind, label: string) => <View style={styles.form}>
    <AppButton label={`Test ${label}`} variant="secondary"
      onPress={() => void runBusy(`test-${kind}`, () => testIntegration(kind))}
      loading={busy === `test-${kind}`} disabled={Boolean(busy)} />
    {testResults[kind] ? <View style={styles.form}>
      <StatusPill label={testResults[kind]!.success ? "Connected" : "Check failed"} tone={testResults[kind]!.success ? "success" : "warning"} />
      <Text style={styles.activeInfo}>{testResults[kind]!.message}</Text>
    </View> : null}
  </View>;

  return (
    <Screen refreshing={busy === "refresh"} onRefresh={() => void runBusy("refresh", load)}>
      <Header
        title="Settings"
        subtitle={settings.display.timeZoneId}
        action={<AppButton label={isDemo ? "Exit demo" : "Logout"} icon={LogOut} onPress={() => void logout()} variant="secondary" compact />}
      />
      <ErrorBanner message={error} />

      <AccountIdentityCard />

      <SectionTitle title="Mobile API" />
      <Card style={styles.form}>
        {isDemo ? <Text style={styles.activeInfo}>Exit demo to connect to a server. Other settings here affect only the sample installation.</Text> : null}
        <TextField label="Base URL" value={baseUrl} onChangeText={setBaseUrl} editable={!isDemo} />
        <AppButton
          label="Save API URL"
          icon={Save}
          onPress={() => void runBusy("api-url", () => updateApiBaseUrl(baseUrl))}
          loading={busy === "api-url"}
          disabled={isDemo}
        />
      </Card>

      <SectionTitle title="Inverter" />
      <Card style={styles.form}>
        <TextField label="Base URL" value={settings.deyeCloud.baseUrl} onChangeText={(baseUrlValue) => patchDeyeCloud({ baseUrl: baseUrlValue })} />
        <TextField label="App ID" value={settings.deyeCloud.appId} onChangeText={(appId) => patchDeyeCloud({ appId })} />
        <TextField label="App Secret" value={settings.deyeCloud.appSecret} onChangeText={(appSecret) => patchDeyeCloud({ appSecret })} secureTextEntry />
        <TextField label="Email" value={settings.deyeCloud.email} onChangeText={(email) => patchDeyeCloud({ email })} />
        <TextField label="Password" value={settings.deyeCloud.password} onChangeText={(password) => patchDeyeCloud({ password })} secureTextEntry />
        <TextField label="Device SN" value={settings.deyeCloud.deviceSn} onChangeText={(deviceSn) => patchDeyeCloud({ deviceSn })} />
        {settings.deyeCloud.deviceSn ? (
          <Text style={styles.activeInfo}>
            {`Active: Station ${settings.deyeCloud.stationId}, Device SN ${settings.deyeCloud.deviceSn}`}
          </Text>
        ) : null}
        <AppButton
          label="Save DeyeCloud"
          icon={Save}
          onPress={() => void runBusy("save-deye", saveDeye)}
          loading={busy === "save-deye"}
        />
        {testAction("deye", "DeyeCloud")}
        <Text style={styles.activeInfo}>Test checks these fields without saving them or changing device state.</Text>
        <AppButton
          label="Fetch Stations"
          icon={RefreshCcw}
          variant="secondary"
          onPress={() => void runBusy("stations", fetchStations)}
          loading={busy === "stations"}
        />
      </Card>

      {stations.length ? (
        <View style={styles.list}>
          {stations.map((station) => (
            <Pressable
              key={station.id}
              onPress={() => setSelectedStationId(station.id)}
              style={[styles.choice, selectedStationId === station.id && styles.choiceSelected]}
            >
              <View style={styles.choiceCopy}>
                <Text style={styles.choiceTitle}>{station.name}</Text>
                <Text style={styles.choiceSubtitle}>{station.address ?? `Station ${station.id}`}</Text>
              </View>
              {selectedStationId === station.id ? <StatusPill label="Selected" tone="success" /> : null}
            </Pressable>
          ))}
          <AppButton
            label="Fetch Devices"
            icon={RefreshCcw}
            variant="secondary"
            disabled={!selectedStationId}
            onPress={() => void runBusy("deye-devices", fetchDeyeDevices)}
            loading={busy === "deye-devices"}
          />
        </View>
      ) : null}

      {deyeDevices.length ? (
        <View style={styles.list}>
          {deyeDevices.map((device) => (
            <Pressable
              key={`${device.stationId}-${device.serialNumber}`}
              onPress={() => void runBusy("select-deye-device", () => selectDeyeDevice(device))}
              style={styles.choice}
            >
              <View style={styles.choiceCopy}>
                <Text style={styles.choiceTitle}>{device.deviceType}</Text>
                <Text style={styles.choiceSubtitle}>{device.serialNumber}</Text>
              </View>
              <StatusPill label="Use" tone="info" />
            </Pressable>
          ))}
        </View>
      ) : null}

      <SectionTitle title="Shelly" />
      <Card style={styles.form}>
        <TextField label="Server URI" value={settings.shelly.serverUri} onChangeText={(serverUri) => patchShelly({ serverUri })} />
        <TextField label="Auth Key" value={settings.shelly.authKey} onChangeText={(authKey) => patchShelly({ authKey })} secureTextEntry />
        <TextField label="Device ID" value={settings.shelly.deviceId} onChangeText={(deviceId) => patchShelly({ deviceId })} />
        <TextField
          label="Request interval ms (100-60000)"
          value={shellyIntervalText}
          keyboardType="number-pad"
          onChangeText={setShellyIntervalText}
        />
        <AppButton
          label="Save Shelly"
          icon={Save}
          onPress={() => void runBusy("save-shelly", saveShelly)}
          loading={busy === "save-shelly"}
        />
        {testAction("shelly", "Shelly")}
        <Text style={styles.activeInfo}>One Shelly cloud key discovers the sockets in that Shelly account. Manage their names in Devices.</Text>
      </Card>

      <Card style={styles.form}>
        <AppButton
          label="Fetch Socket Devices"
          icon={RefreshCcw}
          variant="secondary"
          onPress={() => void runBusy("socket-devices", fetchSocketDevices)}
          loading={busy === "socket-devices"}
        />
        {currentSocketEntityId ? (
          <View style={styles.testRow}>
            <AppButton
              label="Test ON"
              icon={Power}
              onPress={() => void runBusy("test-on", () => testSocket(true))}
              loading={busy === "test-on"}
              compact
            />
            <AppButton
              label="Test OFF"
              icon={Power}
              variant="danger"
              onPress={() => void runBusy("test-off", () => testSocket(false))}
              loading={busy === "test-off"}
              compact
            />
          </View>
        ) : null}
      </Card>

      {socketDevices.length ? (
        <View style={styles.list}>
          {socketDevices.map((device) => (
            <Pressable
              key={device.id}
              onPress={() => void runBusy("select-socket", () => selectSocket(device.id))}
              style={styles.choice}
            >
              <View style={styles.choiceCopy}>
                <Text style={styles.choiceTitle}>{device.name}</Text>
                <Text style={styles.choiceSubtitle}>{device.category ?? device.id}</Text>
              </View>
              <StatusPill label={!device.online ? "Offline" : device.isOn ? "ON" : "OFF"} tone={!device.online ? "neutral" : device.isOn ? "success" : "warning"} />
            </Pressable>
          ))}
        </View>
      ) : null}

      <SectionTitle title="Forecast & sales integrations" />
      <Card style={styles.form}>
        <Text style={styles.activeInfo}>Check the forecast and electricity price providers using the saved server configuration.</Text>
        {site ? <>
          <TextField label="Solar site name" value={site.solarEstimate.locationLabel} onChangeText={locationLabel => setSite(current => current && { ...current, solarEstimate: { ...current.solarEstimate, locationLabel } })} />
          <TextField label="Forecast timezone" value={site.solarEstimate.timeZoneId} onChangeText={timeZoneId => setSite(current => current && { ...current, solarEstimate: { ...current.solarEstimate, timeZoneId } })} />
          {siteNumberFields.map(([key, label]) => <TextField key={key} label={label} value={siteNumbers[key] ?? ""}
            onChangeText={value => setSiteNumbers(current => ({ ...current, [key]: value }))} keyboardType="numbers-and-punctuation" />)}
          <TextField label="Sales contract start (YYYY-MM-DD)" value={site.solarSales.contractStartDate}
            onChangeText={contractStartDate => setSite(current => current && { ...current, solarSales: { ...current.solarSales, contractStartDate } })} />
          <TextField label="Sales timezone" value={site.solarSales.timeZoneId}
            onChangeText={timeZoneId => setSite(current => current && { ...current, solarSales: { ...current.solarSales, timeZoneId } })} />
          <AppButton label={site.solarSales.payNegativePrices ? "Negative sales prices: paid" : "Negative sales prices: floored at zero"}
            variant="secondary" onPress={() => setSite(current => current && { ...current, solarSales: { ...current.solarSales, payNegativePrices: !current.solarSales.payNegativePrices } })} />
          <Text style={styles.activeInfo}>{site.selectedDeviceSn ? `PV source: ${site.selectedDeviceSn}` : "Save/select an inverter above before confirming the PV source."}</Text>
          <AppButton label={site.solarEstimate.deyeSolarPowerIsPvDcConfirmed ? "DC PV source: confirmed" : "Confirm Deye reading is DC PV power"}
            variant="secondary" disabled={Boolean(busy) || !site.selectedDeviceSn}
            onPress={() => setSite(current => current && { ...current, solarEstimate: { ...current.solarEstimate,
              deyeSolarPowerIsPvDcConfirmed: !current.solarEstimate.deyeSolarPowerIsPvDcConfirmed,
              deyeSolarPowerConfirmedDeviceSn: current.solarEstimate.deyeSolarPowerIsPvDcConfirmed ? "" : current.selectedDeviceSn ?? "" } })} />
          <Text style={styles.activeInfo}>Confirm only if the selected inverter reports DC solar-panel power. This enables comparison with the modeled PV generation.</Text>
          <AppButton label="Save solar site & sales" icon={Save} onPress={() => void runBusy("site", saveSite)} loading={busy === "site"} disabled={Boolean(busy)} />
        </> : <Text style={styles.activeInfo}>Site setup will be available after the server supports account installations.</Text>}
        {testAction("openmeteo", "Open-Meteo")}
        {testAction("pse", "PSE")}
      </Card>

      <SectionTitle title="Polling" />
      <Card style={styles.form}>
        <TextField
          label="Interval seconds (5-300)"
          value={pollingIntervalText}
          keyboardType="number-pad"
          onChangeText={setPollingIntervalText}
        />
        <AppButton
          label="Save Polling"
          icon={Save}
          onPress={() => void runBusy("save-polling", savePolling)}
          loading={busy === "save-polling"}
        />
      </Card>

      <SectionTitle title="Display" />
      <Card style={styles.form}>
        <TextField
          label="Timezone"
          value={settings.display.timeZoneId}
          onChangeText={(timeZoneId) =>
            setSettings((current) => current && { ...current, display: { timeZoneId } })
          }
        />
        {deviceTimeZone ? (
          <AppButton
            label={`Use device timezone (${deviceTimeZone})`}
            icon={MapPin}
            variant="secondary"
            onPress={() =>
              setSettings((current) => current && { ...current, display: { timeZoneId: deviceTimeZone } })
            }
          />
        ) : null}
        <AppButton
          label="Save Display"
          icon={Save}
          onPress={() => void runBusy("save-display", saveDisplay)}
          loading={busy === "save-display"}
        />
      </Card>

      {!stations.length && !deyeDevices.length && !socketDevices.length ? (
        <EmptyState title="Cloud discovery results will appear here." />
      ) : null}
    </Screen>
  );
}

const styles = StyleSheet.create({
  form: {
    gap: spacing.lg
  },
  list: {
    gap: spacing.sm
  },
  choice: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: spacing.md,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: colors.border,
    borderRadius: 8,
    backgroundColor: colors.surface,
    padding: spacing.md
  },
  choiceSelected: {
    borderColor: colors.primary,
    backgroundColor: colors.surfaceRaised
  },
  choiceCopy: {
    flex: 1,
    gap: spacing.xs
  },
  choiceTitle: {
    color: colors.text,
    fontSize: typography.body,
    fontWeight: "800"
  },
  choiceSubtitle: {
    color: colors.muted,
    fontSize: typography.caption
  },
  activeInfo: {
    color: colors.muted,
    fontSize: typography.caption
  },
  testRow: {
    flexDirection: "row",
    gap: spacing.md
  }
});
