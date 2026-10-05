import { useScopedAction } from "../../application/useScopedAction";
import type { ScopedActionContext } from "../../application/ScopedActionScope";
import { useDemoDisplayName } from "../demo/useDemoDisplayName";
import { useLanguage } from "../../application/LanguageContext";
import { useCallback, useEffect, useState } from "react";
import { Keyboard, Pressable, StyleSheet, Text, View } from "react-native";
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
import { AccountSecurityCard } from "../auth/AccountSecurityCard";
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
  const demoDisplayName = useDemoDisplayName();
  const { t, language, languages, setLanguage } = useLanguage();
  const { api, apiBaseUrl, isDemo, updateApiBaseUrl, logout } = useAuth();
  const [languageError, setLanguageError] = useState<string | null>(null);
  const [baseUrl, setBaseUrl] = useState(apiBaseUrl);
  const [settings, setSettings] = useState<Settings | null>(null);
  const [pollingIntervalText, setPollingIntervalText] = useState("");
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [testResults, setTestResults] = useState<Partial<Record<IntegrationKind, IntegrationTestResult>>>({});
  const [site, setSite] = useState<SolarSiteSettings | null>(null);
  const [siteNumbers, setSiteNumbers] = useState<Partial<Record<SiteNumberKey, string>>>({});

  const actions = useScopedAction(api, apiBaseUrl, () => {
    setSettings(null); setSite(null); setTestResults({}); setError(null); setLoading(true);
  });
  const busy = actions.busy;
  const load = useCallback(async (context?: ScopedActionContext) => {
    const current = context?.isCurrent ?? actions.capture();
    setError(null);
    try {
      let next = await api.getSettings(context?.signal);
      if (!current()) return;

      // Mirror the web's first-load auto-detect: adopt the device timezone
      // while the stored value is still the "UTC" default.
      if (next.display.timeZoneId === "UTC" && deviceTimeZone && deviceTimeZone !== "UTC") {
        try {
          await api.saveDisplay({ timeZoneId: deviceTimeZone });
          if (!current()) return;
          next = { ...next, display: { timeZoneId: deviceTimeZone } };
        } catch {
          // keep UTC if the server rejects the detected timezone
        }
      }

      if (!current()) return;
      setSettings(next);
      setPollingIntervalText(String(next.polling.intervalSeconds));
      setDisplayTimeZone(next.display.timeZoneId);
      try {
        const nextSite = await api.getSiteSettings(context?.signal);
        if (!current()) return;
        setSite(nextSite);
        setSiteNumbers(Object.fromEntries(siteNumberFields.map(([key]) => [key, String(nextSite.solarEstimate[key])])));
      } catch (ex) {
        if (!current()) return;
        setSite(null);
        setError(ex instanceof Error ? ex.message : t("Settings could not be loaded."));
      }
    } catch (ex) {
      if (current()) setError(ex instanceof Error ? ex.message : "Unable to load settings.");
    } finally {
      if (current()) setLoading(false);
    }
  }, [api, actions.capture]);

  useEffect(() => {
    setLoading(true);
    void load();
  }, [load]);

  const runBusy = (label: string, action: (context: ScopedActionContext) => Promise<void>) => actions.run(label, action, {
    started: () => { Keyboard.dismiss(); setError(null); },
    failed: ex => setError(ex instanceof Error ? ex.message : "Action failed.")
  });

  const languageSettings = <Card style={styles.form}>
    <SectionTitle title={t("Language")} />
    <Text style={styles.activeInfo}>{t("Choose your language for the app and website.")}</Text>
    <View style={styles.languageList}>{languages.map(option => <Pressable key={option.code}
      accessibilityRole="button" accessibilityLabel={option.name} accessibilityState={{ selected: language === option.code }}
      style={[styles.languageChoice, language === option.code && styles.languageSelected]}
      onPress={() => { setLanguageError(null); void setLanguage(option.code).catch(exception => setLanguageError(exception instanceof Error ? exception.message : "Unable to save your language.")); }}>
      <Text style={styles.languageName}>{option.name}</Text>
      {language === option.code ? <StatusPill label={t("Selected")} tone="success" /> : null}
    </Pressable>)}</View>
    <ErrorBanner message={languageError} />
  </Card>;

  if (loading) {
    return (
      <Screen>
        {languageSettings}
        <LoadingState label={t("Loading settings...")} />
      </Screen>
    );
  }

  if (!settings) {
    return (
      <Screen>
        <Header
          title={t("Settings")}
          subtitle={t("Account and installation settings")}
          action={<AppButton label={isDemo ? t("Exit demo") : t("Logout")} icon={LogOut} onPress={() => void logout()} variant="secondary" compact />}
        />
        {languageSettings}
        <ErrorBanner message={error ?? t("Settings could not be loaded.")} />
        <AppButton label={t("Retry")} icon={RefreshCcw} onPress={() => void load()} variant="secondary" />
        <AccountSecurityCard />
      </Screen>
    );
  }

  const savePolling = async (context: ScopedActionContext): Promise<void> => {
    const parsed = Number.parseInt(pollingIntervalText, 10);
    if (Number.isNaN(parsed) || parsed < 5 || parsed > 300) {
      throw new Error(t("Polling interval must be between 5 and 300 seconds."));
    }

    await api.savePolling({ intervalSeconds: parsed });
    context.publish(() => {
      setSettings((current) => current && { ...current, polling: { intervalSeconds: parsed } });
      setPollingIntervalText(String(parsed));
    });
  };

  const saveDisplay = async (context: ScopedActionContext): Promise<void> => {
    const timeZoneId = settings.display.timeZoneId.trim();
    await api.saveDisplay({ timeZoneId });
    context.publish(() => { setSettings((current) => current && { ...current, display: { timeZoneId } }); setDisplayTimeZone(timeZoneId); });
  };

  async function updateSiteSelection() {
    if (!site) return;
    const current = actions.capture();
    const saved = await api.getSiteSettings();
    if (!current()) return;
    setSite(current => current && { ...current, selectedDeviceSn: saved.selectedDeviceSn,
      solarEstimate: { ...current.solarEstimate,
        deyeSolarPowerIsPvDcConfirmed: saved.solarEstimate.deyeSolarPowerIsPvDcConfirmed,
        deyeSolarPowerConfirmedDeviceSn: saved.solarEstimate.deyeSolarPowerConfirmedDeviceSn } });
  }

  const testIntegration = async (kind: IntegrationKind, context: ScopedActionContext): Promise<void> => {
    setTestResults(current => ({ ...current, [kind]: undefined }));
    const draft = kind === "openmeteo" && site ? { solarEstimate: { latitude: siteNumber("latitude"), longitude: siteNumber("longitude") } } : {};
    const result = await api.testIntegration(kind, draft);
    context.publish(() => setTestResults(current => ({ ...current, [kind]: result })));
  };

  function siteNumber(key: SiteNumberKey): number {
    const text = siteNumbers[key]?.trim();
    const value = text ? Number(text.replace(",", ".")) : Number.NaN;
    if (!Number.isFinite(value)) throw new Error(t("Enter valid numbers for the solar site."));
    return value;
  }

  async function saveSite(context: ScopedActionContext) {
    if (!site) return;
    const solarEstimate = { ...site.solarEstimate };
    for (const [key] of siteNumberFields) solarEstimate[key] = siteNumber(key);
    const next = { ...site, solarEstimate };
    await api.saveSiteSettings(next);
    context.publish(() => setSite(next));
  }

  const testAction = (kind: IntegrationKind, label: string) => <View style={styles.form}>
    <AppButton label={t("Test {0}", label)} variant="secondary"
      onPress={() => void runBusy(`test-${kind}`, context => testIntegration(kind, context))}
      loading={busy === `test-${kind}`} disabled={Boolean(busy)} />
    {testResults[kind] ? <View style={styles.form}>
      <StatusPill label={testResults[kind]!.success ? t("Connected") : t("Check failed")} tone={testResults[kind]!.success ? "success" : "warning"} />
      <Text style={styles.activeInfo}>{t(testResults[kind]!.message)}</Text>
    </View> : null}
  </View>;

  return (
    <Screen refreshing={busy === "refresh"} onRefresh={() => void runBusy("refresh", load)}>
      <Header
        title={t("Settings")}
        subtitle={settings.display.timeZoneId}
        action={<AppButton label={isDemo ? t("Exit demo") : t("Logout")} icon={LogOut} onPress={() => void logout()} variant="secondary" compact />}
      />
      <ErrorBanner message={error} />

      {languageSettings}
      <AccountIdentityCard />
      <AccountSecurityCard />

      <SectionTitle title={t("Mobile API")} />
      <Card style={styles.form}>
        {isDemo ? <Text style={styles.activeInfo}>{t("Exit demo to connect to a server. Other settings here affect only the sample installation.")}</Text> : null}
        <TextField label={t("Base URL")} value={baseUrl} onChangeText={setBaseUrl} editable={!isDemo} />
        <AppButton
          label={t("Save API URL")}
          icon={Save}
          onPress={() => void runBusy("api-url", () => updateApiBaseUrl(baseUrl))}
          loading={busy === "api-url"}
          disabled={isDemo}
        />
      </Card>

      <IntegrationSettings api={api.integrations} isDemo={isDemo} onSelectionChanged={updateSiteSelection} />

      <SectionTitle title={t("Forecast & sales integrations")} />
      <Card style={styles.form}>
        <Text style={styles.activeInfo}>{t("Check the forecast and electricity price providers using the saved server configuration.")}</Text>
        {site ? <>
          <TextField label={t("Solar site name")} value={demoDisplayName(site.solarEstimate.locationLabel)} onChangeText={locationLabel => setSite(current => current && { ...current, solarEstimate: { ...current.solarEstimate, locationLabel } })} />
          <TextField label={t("Forecast timezone")} value={site.solarEstimate.timeZoneId} onChangeText={timeZoneId => setSite(current => current && { ...current, solarEstimate: { ...current.solarEstimate, timeZoneId } })} />
          {siteNumberFields.map(([key, label]) => <TextField key={key} label={t(label)} value={siteNumbers[key] ?? ""}
            onChangeText={value => setSiteNumbers(current => ({ ...current, [key]: value }))} keyboardType="numbers-and-punctuation" />)}
          <TextField label={t("Sales contract start (YYYY-MM-DD)")} value={site.solarSales.contractStartDate}
            onChangeText={contractStartDate => setSite(current => current && { ...current, solarSales: { ...current.solarSales, contractStartDate } })} />
          <TextField label={t("Sales timezone")} value={site.solarSales.timeZoneId}
            onChangeText={timeZoneId => setSite(current => current && { ...current, solarSales: { ...current.solarSales, timeZoneId } })} />
          <AppButton label={site.solarSales.payNegativePrices ? t("Negative sales prices: paid") : t("Negative sales prices: floored at zero")}
            variant="secondary" onPress={() => setSite(current => current && { ...current, solarSales: { ...current.solarSales, payNegativePrices: !current.solarSales.payNegativePrices } })} />
          <Text style={styles.activeInfo}>{site.selectedDeviceSn ? t("PV source: {0}", site.selectedDeviceSn) : t("Save/select an inverter above before confirming the PV source.")}</Text>
          <AppButton label={site.solarEstimate.deyeSolarPowerIsPvDcConfirmed ? t("DC PV source: confirmed") : t("Confirm inverter reading is DC PV power")}
            variant="secondary" disabled={Boolean(busy) || !site.selectedDeviceSn}
            onPress={() => setSite(current => current && { ...current, solarEstimate: { ...current.solarEstimate,
              deyeSolarPowerIsPvDcConfirmed: !current.solarEstimate.deyeSolarPowerIsPvDcConfirmed,
              deyeSolarPowerConfirmedDeviceSn: current.solarEstimate.deyeSolarPowerIsPvDcConfirmed ? "" : current.selectedDeviceSn ?? "" } })} />
          <Text style={styles.activeInfo}>{t("Confirm only if the selected inverter reports DC solar-panel power. This enables comparison with the modeled PV generation.")}</Text>
          <AppButton label={t("Save solar site & sales")} icon={Save} onPress={() => void runBusy("site", saveSite)} loading={busy === "site"} disabled={Boolean(busy)} />
        </> : <Text style={styles.activeInfo}>{t("Settings could not be loaded.")}</Text>}
        {testAction("openmeteo", "Open-Meteo")}
        {testAction("pse", "PSE")}
      </Card>

      <SectionTitle title={t("Polling")} />
      <Card style={styles.form}>
        <TextField
          label={t("Interval seconds (5-300)")}
          value={pollingIntervalText}
          keyboardType="number-pad"
          onChangeText={setPollingIntervalText}
        />
        <AppButton
          label={t("Save Polling")}
          icon={Save}
          onPress={() => void runBusy("save-polling", savePolling)}
          loading={busy === "save-polling"}
        />
      </Card>

      <SectionTitle title={t("Display")} />
      <Card style={styles.form}>
        <TextField
          label={t("Timezone")}
          value={settings.display.timeZoneId}
          onChangeText={(timeZoneId) =>
            setSettings((current) => current && { ...current, display: { timeZoneId } })
          }
        />
        {deviceTimeZone ? (
          <AppButton
            label={t("Use device timezone ({0})", deviceTimeZone)}
            icon={MapPin}
            variant="secondary"
            onPress={() =>
              setSettings((current) => current && { ...current, display: { timeZoneId: deviceTimeZone } })
            }
          />
        ) : null}
        <AppButton
          label={t("Save Display")}
          icon={Save}
          onPress={() => void runBusy("save-display", saveDisplay)}
          loading={busy === "save-display"}
        />
      </Card>
    </Screen>
  );
}

const styles = StyleSheet.create({
  languageList: { gap: spacing.sm },
  languageChoice: { minHeight: 44, flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: spacing.sm, padding: spacing.sm, borderWidth: 1, borderColor: colors.border, borderRadius: 8 },
  languageSelected: { borderColor: colors.primary },
  languageName: { color: colors.text, fontSize: typography.body, flexShrink: 1 },
  form: {
    gap: spacing.lg
  },
  activeInfo: {
    color: colors.muted,
    fontSize: typography.caption
  }
});
