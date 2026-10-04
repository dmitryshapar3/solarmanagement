import { useCallback, useEffect, useState } from "react";
import { StyleSheet, Text, View } from "react-native";
import { RefreshCcw } from "lucide-react-native";
import { useAuth } from "../../application/AuthContext";
import { AppButton, Card, EmptyState, ErrorBanner, Header, LoadingState, ProgressBar, Screen, SectionTitle } from "../../core/components";
import { batteryModeLabel, formatDateTime, formatPercent, formatSignedWatts, formatWatts, gridModeLabel, setDisplayTimeZone } from "../../core/format";
import { colors, spacing, typography } from "../../core/theme";
import { useFocusedResource } from "../energy/useFocusedResource";
import { balanceDirection, batteryFlow, formatBalanceWatts, hasSourceMetadata, reportedPowerBalance } from "./powerBalance";

export function InverterDetailsScreen() {
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
    <Header title="Inverter details" subtitle="Solar generation, battery and energy flows" />
    <AppButton label="Refresh" accessibilityLabel="Inverter details refresh" icon={RefreshCcw} loading={resource.loading} onPress={() => void resource.refresh(true)} variant="secondary" compact />
    <ErrorBanner message={resource.error} />
    {resource.loading && !resource.data ? <LoadingState label="Loading inverter readings..." /> : !inverter ?
      <EmptyState title="No inverter reading available." detail="Refresh to request the latest inverter data." /> : <>
      <Card style={styles.card}>
        <Text style={styles.caption}>Latest reported solar power</Text>
        <Text style={styles.hero}>{formatWatts(inverter.solarPowerValid === false ? null : inverter.solarProduction)}</Text>
        <Text style={styles.note}>Polled {formatDateTime(inverter.timestamp)}</Text>
        {inverter.solarObservedAt ? <Text style={styles.note}>Solar measured {formatDateTime(inverter.solarObservedAt)}</Text> : null}
        {inverter.gridObservedAt ? <Text style={styles.note}>Grid measured {formatDateTime(inverter.gridObservedAt)}</Text> : null}
        <Text style={styles.note}>Source: {inverter.dataSource || "inverter"}</Text>
        <Text style={styles.note}>Polling time may be later than the inverter measurement time. Individual panel readings are not available here. Unavailable measurements are shown with a dash.</Text>
        {inverter.batteryPowerValid == null ? <Text style={styles.note}>This older server does not identify missing battery and load readings; a reported zero may mean that a measurement was not supplied.</Text> : null}
      </Card>
      <SectionTitle title="Battery" />
      <Card style={styles.card}>
        <Metric label="State of charge" value={formatPercent(inverter.batterySocValid === false ? null : inverter.batterySoc)} />
        {inverter.batterySocValid !== false ? <ProgressBar value={inverter.batterySoc} /> : null}
        <Metric label="Power" value={formatSignedWatts(inverter.batteryPowerValid === false ? null : inverter.batteryPower)}
          detail={inverter.batteryPowerValid === false ? "Unavailable" : batteryModeLabel(inverter.batteryPower)} />
        <Metric label="Voltage" value={quantity(inverter.batteryVoltageValid === false ? null : inverter.batteryVoltage, "V")} />
        <Metric label="Current" value={quantity(inverter.batteryCurrentValid === false ? null : inverter.batteryCurrent, "A")} />
        <Metric label="Temperature" value={quantity(inverter.batteryTemperatureValid === false ? null : inverter.batteryTemperature, "°C")} />
        <Text style={styles.note}>Negative battery power means charging; positive means discharging.</Text>
      </Card>
      <SectionTitle title="Energy flows" />
      <Card style={styles.card}>
        <Metric label="Solar generation" value={formatWatts(inverter.solarPowerValid === false ? null : inverter.solarProduction)} />
        <Metric label="Load" value={formatWatts(inverter.loadPowerValid === false ? null : inverter.loadPower)} />
        <Metric label={battery.label} value={battery.watts === null ? "—" : formatWatts(battery.watts)} />
        <Metric label="Grid power" value={formatSignedWatts(inverter.gridPowerValid === false ? null : inverter.gridConsumption)}
          detail={inverter.gridPowerValid === false ? "Unavailable" : gridModeLabel(inverter.gridConsumption)} />
        <Text style={styles.note}>Negative grid power means export; positive means import. These are instantaneous power readings, not accumulated energy.</Text>
      </Card>
      <SectionTitle title="Power balance" />
      <Card style={styles.card}>
        <Metric label="Balance difference" value={formatBalanceWatts(balance.watts)}
          detail={balance.watts === null ? balance.reason ?? "Unavailable" : balanceDirection(balance.watts)} />
        <Text style={styles.note}>Solar + signed grid + signed battery − load. Grid import and battery discharge are positive; export and charging are negative.</Text>
        {!hasSourceMetadata(inverter) ? <Text style={styles.note}>This server supplies polling time only; source measurement times cannot be checked.</Text> : null}
        <Text style={styles.note}>This is an approximate balance of reported values. Measurements may be taken at different times. Conversion and measurement differences also contribute; this is not a measurement of inverter losses.</Text>
      </Card>
    </>}
  </Screen>;
}

function quantity(value: number | null, unit: string) {
  return value !== null && Number.isFinite(value) ? `${Number(value.toFixed(2))} ${unit}` : "—";
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
