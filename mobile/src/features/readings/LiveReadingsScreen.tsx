import { formattingLocale } from "../../core/i18n";
import { useLanguage } from "../../application/LanguageContext";
import { useCallback, useEffect, useState } from "react";
import { StyleSheet, View } from "react-native";
import { useNavigation } from "@react-navigation/native";
import type { NavigationProp } from "@react-navigation/native";
import type { HomeStackParamList } from "../../application/navigationTypes";
import { useLegacyTheme } from "../../ui/theme/ThemeProvider";
import { BatteryChargeBar } from "../../ui/charts/BatteryChargeBar";
import { ReadingTrend } from "../readings/ReadingTrend";
import { freshnessLabel } from "../../core/freshness";
import { useAuth } from "../../application/AuthContext";
import { Card, EmptyState, ErrorBanner, Header, LoadingState, Screen, SectionTitle, NavigationRow, ThemedText as Text } from "../../core/components";
import { batteryModeLabel, formatDateTime, formatPercent, formatSignedWatts, formatWatts, gridModeLabel, setDisplayTimeZone } from "../../core/format";
import { colors, spacing, typography } from "../../core/theme";
import { useFocusedResource } from "../energy/useFocusedResource";
import { balanceDirection, batteryFlow, formatBalanceWatts, reportedPowerBalance } from "../dashboard/powerBalance";

export function LiveReadingsScreen() {
  const { t } = useLanguage();
  const { colors } = useLegacyTheme(); const styles = makeStyles(colors);
  const navigation = useNavigation<NavigationProp<HomeStackParamList>>();
  const { api } = useAuth();
  const resource = useFocusedResource("inverter-details", useCallback((signal: AbortSignal, force: boolean) =>
    force ? api.refreshDashboard(signal) : api.getDashboard(signal), [api]), api);
  const history = useFocusedResource("live-readings-6h", useCallback((signal: AbortSignal) => api.getReadingsView(6, "5m", undefined, signal), [api]), api);
  const inverter = resource.data?.inverter;
  const rules = resource.data?.rules ?? [];
  const sourceFacts = useFocusedResource(`live-rule-sources:${rules.map(rule=>rule.entityId).join(",")}`, useCallback(async (signal: AbortSignal) => {
    const [sources, details] = await Promise.all([api.integrations.getSocketSources(signal), Promise.allSettled([...new Set(rules.map(rule=>rule.entityId))].map(id=>api.getDeviceDetails(id,signal)))]);
    return { defaultId: sources.find(source=>source.isDefault)?.id, details: details.flatMap(result=>result.status === "fulfilled" ? [result.value] : []) };
  }, [api, rules.map(rule=>rule.entityId).join(",")]), api);
  const relevantRules = rules.filter(rule=>rule.enabled && inverter?.inverterId && (rule.sourceInverterId ? rule.sourceInverterId === inverter.inverterId : sourceFacts.data?.details.some(detail=>detail.id === rule.entityId && (detail.sourceInverterId ?? sourceFacts.data?.defaultId) === inverter.inverterId)));
  const [, setClock] = useState(Date.now);
  useEffect(() => {
    const timer = setInterval(() => setClock(Date.now()), 30_000);
    return () => clearInterval(timer);
  }, []);
  const battery = batteryFlow(inverter?.batteryPower, inverter?.batteryPowerValid);
  const balance = reportedPowerBalance(inverter);
  useEffect(() => { if (resource.data) setDisplayTimeZone(resource.data.timeZoneId); }, [resource.data]);
  return <Screen refreshing={resource.loading || history.loading || sourceFacts.loading} onRefresh={async () => { await Promise.all([resource.refresh(true), history.refresh(true), sourceFacts.refresh(true)]); }}>
    <Header title={t("Live readings")} subtitle={freshnessLabel(inverter?.timestamp, Boolean(resource.error), t)} />
    <ErrorBanner message={resource.error} />
    {resource.loading && !resource.data ? <LoadingState label={t("Loading inverter readings...")} /> : !inverter ?
      <EmptyState title={t("No inverter reading available.")} detail={t("Pull down to request the latest inverter data.")} /> : <>
      <Card style={styles.card}>
        <Text style={styles.caption}>{t("Latest reported solar power")}</Text>
        <Text style={styles.hero}>{formatWatts(inverter.solarPowerValid !== true ? null : inverter.solarProduction)}</Text>
        <Text style={styles.note}>{t("Polled {0}", formatDateTime(inverter.timestamp))}</Text>
        {inverter.solarObservedAt ? <Text style={styles.note}>{t("Solar measured {0}", formatDateTime(inverter.solarObservedAt))}</Text> : null}
        {inverter.gridObservedAt ? <Text style={styles.note}>{t("Grid measured {0}", formatDateTime(inverter.gridObservedAt))}</Text> : null}
        <Text style={styles.note}>{t("Source: {0}", t(inverter.dataSource || "Inverter"))}</Text>
        <Text style={styles.note}>{t("Polling time may be later than the inverter measurement time. Individual panel readings are not available here. Unavailable measurements are shown with a dash.")}</Text>
      </Card>
      <SectionTitle title={t("Battery")} />
      <Card style={styles.card}>
        <Metric label={t("State of charge")} value={formatPercent(inverter.batterySocValid !== true ? null : inverter.batterySoc)} />
        {inverter.batterySocValid === true ? <BatteryChargeBar charge={inverter.batterySoc} rules={relevantRules} /> : null}
        <Metric label={t("Power")} value={formatSignedWatts(inverter.batteryPowerValid !== true ? null : inverter.batteryPower)}
          detail={inverter.batteryPowerValid !== true ? t("Unavailable") : batteryModeLabel(inverter.batteryPower)} />
        <Metric label={t("Voltage")} value={quantity(inverter.batteryVoltageValid !== true ? null : inverter.batteryVoltage, "V")} />
        <Metric label={t("Current")} value={quantity(inverter.batteryCurrentValid !== true ? null : inverter.batteryCurrent, "A")} />
        <Metric label={t("Temperature")} value={quantity(inverter.batteryTemperatureValid !== true ? null : inverter.batteryTemperature, "°C")} />
        {history.data?.items.length ? <><ReadingTrend rows={history.data.items} /><Text style={styles.note}>{t("Battery charge · last 6 hours")}</Text></> : null}
        <Text style={styles.note}>{t("Negative battery power means charging; positive means discharging.")}</Text>
      </Card>
      <SectionTitle title={t("Energy flows")} />
      <Card style={styles.card}>
        <Metric label={t("Solar generation")} value={formatWatts(inverter.solarPowerValid !== true ? null : inverter.solarProduction)} />
        <Metric label={t("Load")} value={formatWatts(inverter.loadPowerValid !== true ? null : inverter.loadPower)} />
        <Metric label={battery.label} value={battery.watts === null ? "—" : formatWatts(battery.watts)} />
        <Metric label={t("Grid power")} value={formatSignedWatts(inverter.gridPowerValid !== true ? null : inverter.gridConsumption)}
          detail={inverter.gridPowerValid !== true ? t("Unavailable") : gridModeLabel(inverter.gridConsumption)} />
        <Text style={styles.note}>{t("Negative grid power means export; positive means import. These are instantaneous power readings, not accumulated energy.")}</Text>
      </Card>
      <SectionTitle title={t("Power balance")} />
      <Card style={styles.card}>
        <Metric label={t("Balance difference")} value={formatBalanceWatts(balance.watts)}
          detail={balance.watts === null ? balance.reason ?? t("Unavailable") : balanceDirection(balance.watts)} />
        <Text style={styles.note}>{t("Solar + signed grid + signed battery − load. Grid import and battery discharge are positive; export and charging are negative.")}</Text>
        <Text style={styles.note}>{t("This is an approximate balance of reported values. Measurements may be taken at different times. Conversion and measurement differences also contribute; this is not a measurement of inverter losses.")}</Text>
      </Card>
      <NavigationRow title="Readings log" subtitle="Last 6 hours · raw readings and 5-minute averages" onPress={() => navigation.navigate("ReadingsLog")} />
      <NavigationRow title="Primary inverter" subtitle="Choose the default in Solar site settings" onPress={() => navigation.navigate("SolarSite")} />
      <ErrorBanner message={history.error} />
    </>}
  </Screen>;
}

function quantity(value: number | null, unit: string) {
  return value !== null && Number.isFinite(value) ? `${value.toLocaleString(formattingLocale(), { maximumFractionDigits: 2 })} ${unit}` : "—";
}

function Metric({ label, value, detail }: { label: string; value: string; detail?: string }) {
  const { t } = useLanguage(); const { colors } = useLegacyTheme(); const styles = makeStyles(colors);
  return <View style={styles.metric}>
    <View style={styles.copy}><Text style={styles.label}>{t(label)}</Text>{detail ? <Text style={styles.note}>{t(detail)}</Text> : null}</View>
    <Text style={styles.value}>{t(value)}</Text>
  </View>;
}

const makeStyles = (colors: ReturnType<typeof useLegacyTheme>["colors"]) => StyleSheet.create({
  card: { gap: spacing.md },
  caption: { color: colors.muted, fontSize: typography.body },
  hero: { color: colors.text, fontSize: 52, lineHeight: 58, fontWeight: "700", fontVariant: ["tabular-nums"] },
  metric: { flexDirection: "row", flexWrap: "wrap", justifyContent: "space-between", alignItems: "center", gap: spacing.sm },
  copy: { gap: spacing.xs },
  label: { color: colors.text, fontSize: typography.body },
  value: { color: colors.text, fontSize: 20, fontWeight: "700", fontVariant: ["tabular-nums"] },
  note: { color: colors.muted, fontSize: typography.caption, lineHeight: 18 }
});
