import { useCallback, useEffect } from "react";
import { StyleSheet, Text, View } from "react-native";
import { RefreshCcw } from "lucide-react-native";
import { useAuth } from "../../application/AuthContext";
import { AppButton, Card, EmptyState, ErrorBanner, Header, LoadingState, ProgressBar, Screen, SectionTitle } from "../../core/components";
import { batteryModeLabel, formatDateTime, formatPercent, formatSignedWatts, formatWatts, gridModeLabel, setDisplayTimeZone } from "../../core/format";
import { colors, spacing, typography } from "../../core/theme";
import { useFocusedResource } from "../energy/useFocusedResource";

export function InverterDetailsScreen() {
  const { api } = useAuth();
  const resource = useFocusedResource("inverter-details", useCallback((signal: AbortSignal, force: boolean) =>
    force ? api.refreshDashboard(signal) : api.getDashboard(signal), [api]));
  const inverter = resource.data?.inverter;
  useEffect(() => { if (resource.data) setDisplayTimeZone(resource.data.timeZoneId); }, [resource.data]);
  return <Screen refreshing={resource.loading} onRefresh={() => void resource.refresh(true)}>
    <Header title="Inverter details" subtitle="Solar generation, battery and energy flows" />
    <AppButton label="Refresh" accessibilityLabel="Inverter details refresh" icon={RefreshCcw} loading={resource.loading} onPress={() => void resource.refresh(true)} variant="secondary" compact />
    <ErrorBanner message={resource.error} />
    {resource.loading && !resource.data ? <LoadingState label="Loading inverter readings..." /> : !inverter ?
      <EmptyState title="No Deye reading available." detail="Refresh to request the latest inverter data." /> : <>
      <Card style={styles.card}>
        <Text style={styles.caption}>Latest reported solar power</Text>
        <Text style={styles.hero}>{formatWatts(inverter.solarProduction)}</Text>
        <Text style={styles.note}>Polled {formatDateTime(inverter.timestamp)}</Text>
        <Text style={styles.note}>Source: {inverter.dataSource || "Deye"}</Text>
        <Text style={styles.note}>Polling time is shown. The inverter's measurement time and individual panel readings are not available here. A zero in these reported values may also mean that Deye did not provide that reading.</Text>
      </Card>
      <SectionTitle title="Battery" />
      <Card style={styles.card}>
        <Metric label="State of charge" value={formatPercent(inverter.batterySoc)} />
        <ProgressBar value={inverter.batterySoc} />
        <Metric label="Power" value={formatSignedWatts(inverter.batteryPower)} detail={batteryModeLabel(inverter.batteryPower)} />
        <Metric label="Voltage" value={quantity(inverter.batteryVoltage, "V")} />
        <Metric label="Current" value={quantity(inverter.batteryCurrent, "A")} />
        <Metric label="Temperature" value={quantity(inverter.batteryTemperature, "°C")} />
        <Text style={styles.note}>Negative battery power means charging; positive means discharging.</Text>
      </Card>
      <SectionTitle title="Energy flows" />
      <Card style={styles.card}>
        <Metric label="Solar generation" value={formatWatts(inverter.solarProduction)} />
        <Metric label="Load" value={formatWatts(inverter.loadPower)} />
        <Metric label={inverter.batteryPower < 0 ? "Battery charging" : inverter.batteryPower > 0 ? "Battery discharging" : "Battery idle"}
          value={formatWatts(Math.abs(inverter.batteryPower))} />
        <Metric label="Grid power" value={formatSignedWatts(inverter.gridConsumption)} detail={gridModeLabel(inverter.gridConsumption)} />
        <Text style={styles.note}>Negative grid power means export; positive means import. These are instantaneous power readings, not accumulated energy.</Text>
      </Card>
    </>}
  </Screen>;
}

function quantity(value: number, unit: string) {
  return Number.isFinite(value) ? `${Number(value.toFixed(2))} ${unit}` : "—";
}

function Metric({ label, value, detail }: { label: string; value: string; detail?: string }) {
  return <View style={styles.metric}>
    <View style={styles.copy}><Text style={styles.label}>{label}</Text>{detail ? <Text style={styles.note}>{detail}</Text> : null}</View>
    <Text style={styles.value}>{value}</Text>
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
