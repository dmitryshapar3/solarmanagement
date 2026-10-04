import { formattingLocale } from "../../core/i18n";
import { useLanguage } from "../../application/LanguageContext";
import { useCallback, useEffect, useState } from "react";
import { StyleSheet, Text, View } from "react-native";
import { RefreshCcw } from "lucide-react-native";
import { useAuth } from "../../application/AuthContext";
import { AppButton, Card, EmptyState, ErrorBanner, Header, LoadingState, ProgressBar, Screen, SectionTitle } from "../../core/components";
import { batteryModeLabel, formatDateTime, formatPercent, formatSignedWatts, formatWatts, gridModeLabel, setDisplayTimeZone } from "../../core/format";
import { colors, spacing, typography } from "../../core/theme";
import { useFocusedResource } from "../energy/useFocusedResource";
import { balanceDirection, batteryFlow, formatBalanceWatts, reportedPowerBalance } from "./powerBalance";

export function InverterDetailsScreen() {
  const { t } = useLanguage();
  const { api } = useAuth();
  const resource = useFocusedResource("inverter-details", useCallback((signal: AbortSignal, force: boolean) =>
    force ? api.refreshDashboard(signal) : api.getDashboard(signal), [api]));
  const inverter = resource.data?.inverter;
  const [, setClock] = useState(Date.now);
  useEffect(() => {
    const timer = setInterval(() => setClock(Date.now()), 30_000);
    return () => clearInterval(timer);
  }, []);
  const battery = batteryFlow(inverter?.batteryPower, inverter?.batteryPowerValid);
  const balance = reportedPowerBalance(inverter);
  useEffect(() => { if (resource.data) setDisplayTimeZone(resource.data.timeZoneId); }, [resource.data]);
  return <Screen refreshing={resource.loading} onRefresh={() => void resource.refresh(true)}>
    <Header title={t("Inverter details")} subtitle={t("Solar generation, battery and energy flows")} />
    <AppButton label={t("Refresh")} accessibilityLabel={t("Inverter details refresh")} icon={RefreshCcw} loading={resource.loading} onPress={() => void resource.refresh(true)} variant="secondary" compact />
    <ErrorBanner message={resource.error} />
    {resource.loading && !resource.data ? <LoadingState label={t("Loading inverter readings...")} /> : !inverter ?
      <EmptyState title={t("No inverter reading available.")} detail={t("Refresh to request the latest inverter data.")} /> : <>
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
        {inverter.batterySocValid === true ? <ProgressBar value={inverter.batterySoc} /> : null}
        <Metric label={t("Power")} value={formatSignedWatts(inverter.batteryPowerValid !== true ? null : inverter.batteryPower)}
          detail={inverter.batteryPowerValid !== true ? t("Unavailable") : batteryModeLabel(inverter.batteryPower)} />
        <Metric label={t("Voltage")} value={quantity(inverter.batteryVoltageValid !== true ? null : inverter.batteryVoltage, "V")} />
        <Metric label={t("Current")} value={quantity(inverter.batteryCurrentValid !== true ? null : inverter.batteryCurrent, "A")} />
        <Metric label={t("Temperature")} value={quantity(inverter.batteryTemperatureValid !== true ? null : inverter.batteryTemperature, "°C")} />
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
    </>}
  </Screen>;
}

function quantity(value: number | null, unit: string) {
  return value !== null && Number.isFinite(value) ? `${value.toLocaleString(formattingLocale(), { maximumFractionDigits: 2 })} ${unit}` : "—";
}

function Metric({ label, value, detail }: { label: string; value: string; detail?: string }) {
  const { t } = useLanguage();
  return <View style={styles.metric}>
    <View style={styles.copy}><Text style={styles.label}>{t(label)}</Text>{detail ? <Text style={styles.note}>{t(detail)}</Text> : null}</View>
    <Text style={styles.value}>{t(value)}</Text>
  </View>;
}

const styles = StyleSheet.create({
  card: { gap: spacing.md },
  caption: { color: colors.muted, fontSize: typography.body },
  hero: { color: colors.primary, fontSize: 36, fontWeight: "800", fontVariant: ["tabular-nums"] },
  metric: { flexDirection: "row", flexWrap: "wrap", justifyContent: "space-between", alignItems: "center", gap: spacing.sm },
  copy: { gap: spacing.xs },
  label: { color: colors.text, fontSize: typography.body },
  value: { color: colors.text, fontSize: 20, fontWeight: "700", fontVariant: ["tabular-nums"] },
  note: { color: colors.muted, fontSize: typography.caption, lineHeight: 18 }
});
