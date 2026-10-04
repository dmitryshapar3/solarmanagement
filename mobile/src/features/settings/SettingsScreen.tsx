import { useCallback, useEffect, useRef, useState } from "react";
import { Keyboard, StyleSheet, Text, View } from "react-native";
import { LogOut, MapPin, RefreshCcw, Save } from "lucide-react-native";
import {
  AppButton,
  Card,
  ErrorBanner,
  Header,
  LoadingState,
  Screen,
  SectionTitle,
  StatusPill,
  TextField
} from "../../core/components";
import type { Settings } from "../../core/api/types";
import type { IntegrationKind, IntegrationTestResult, SolarSiteSettings } from "../../core/api/types";
import { setDisplayTimeZone } from "../../core/format";
import { colors, spacing, typography } from "../../core/theme";
import { useAuth } from "../../application/AuthContext";
import { AccountIdentityCard } from "../auth/AccountIdentityCard";
import { IntegrationSettings } from "../integrations/IntegrationSettings";

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
  const [pollingIntervalText, setPollingIntervalText] = useState("");
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [testResults, setTestResults] = useState<Partial<Record<IntegrationKind, IntegrationTestResult>>>({});
  const actionPending = useRef(false);
  const [site, setSite] = useState<SolarSiteSettings | null>(null);
  const [siteNumbers, setSiteNumbers] = useState<Partial<Record<SiteNumberKey, string>>>({});

  const load = useCallback(async () => {
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
      setPollingIntervalText(String(next.polling.intervalSeconds));
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
    setLoading(true);
    void load();
  }, [load]);

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

  const testIntegration = async (kind: IntegrationKind): Promise<void> => {
    setTestResults(current => ({ ...current, [kind]: undefined }));
    const draft = kind === "openmeteo" && site ? { solarEstimate: { latitude: siteNumber("latitude"), longitude: siteNumber("longitude") } } : {};
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

      <IntegrationSettings api={api.integrations} isDemo={isDemo} onSelectionChanged={updateSiteSelection} />

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
          <AppButton label={site.solarEstimate.deyeSolarPowerIsPvDcConfirmed ? "DC PV source: confirmed" : "Confirm inverter reading is DC PV power"}
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
    </Screen>
  );
}

const styles = StyleSheet.create({
  form: {
    gap: spacing.lg
  },
  activeInfo: {
    color: colors.muted,
    fontSize: typography.caption
  }
});
