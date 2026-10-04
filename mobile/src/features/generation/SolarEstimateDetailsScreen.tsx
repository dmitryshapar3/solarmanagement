import { useCallback, useRef } from "react";
import { Linking, Pressable, Text, View } from "react-native";
import { RouteProp, useRoute } from "@react-navigation/native";
import { useAuth } from "../../application/AuthContext";
import { RootStackParamList } from "../../application/navigationTypes";
import { Card, ErrorBanner, Header, LoadingState, Screen, StatusPill } from "../../core/components";
import { SolarPowerBasis } from "../../core/api/types";
import { TileHeader } from "../../core/TileHeader";
import { energyStyles as styles } from "../energy/EnergyControls";
import { amount, momentCaption } from "../energy/chartPolicy";
import { useFocusedResource } from "../energy/useFocusedResource";
import { CurrentSolarSnapshot, GenerationHistoryPanel } from "./GenerationScreen";

const powerBasis: Record<SolarPowerBasis, string> = { 0: "PV DC generation", 1: "Inverter AC power", 2: "Grid export" };
const comparisonStatus = ["Comparison unavailable", "Within estimate range", "Below estimate range", "Above estimate range"];

export function SolarEstimateDetailsScreen() {
  const { api, isDemo } = useAuth();
  const route = useRoute<RouteProp<RootStackParamList, "SolarEstimateDetails">>();
  const refreshPending = useRef(false);
  const estimate = useFocusedResource("solar-estimate-details", useCallback((signal: AbortSignal) => api.getSolarEstimate(signal), [api]));
  const inverter = useFocusedResource("solar-estimate-dashboard", useCallback((signal: AbortSignal, force: boolean) =>
    force ? api.refreshDashboard(signal) : api.getDashboard(signal), [api]));
  const state = estimate.data;
  const currentEstimate = state?.estimate;
  const actual = state?.comparison.actual;
  const matchedEstimate = state?.comparisonEstimate ?? (currentEstimate?.observation.kind === 0 ? currentEstimate : null);
  const timeZone = inverter.data?.timeZoneId ?? "Europe/Warsaw";
  const loading = estimate.loading || inverter.loading;
  const verified = state && !state.error && !state.refreshFailed && state.comparison.status !== 0;
  async function refresh() {
    if (loading || refreshPending.current) return;
    refreshPending.current = true;
    try { await Promise.all([estimate.refresh(), inverter.refresh(true)]); }
    finally { refreshPending.current = false; }
  }
  return <Screen refreshing={loading} onRefresh={() => void refresh()}>
    <Header title="Solar estimate details" subtitle="Hourly actual and possible power, weather calculation and measurement comparison" />
    <GenerationHistoryPanel initialPeriod={route.params?.period} initialDate={route.params?.date} detailed
      onRefreshSnapshot={refresh} snapshotLoading={loading} />
    <CurrentSolarSnapshot state={state} liveInverter={inverter.data?.inverter} timeZoneId={timeZone}
      error={estimate.error ?? inverter.error} loading={loading} onRefresh={() => void refresh()} />
    <Text style={styles.muted}>inverter measurement time is not available in the latest reading; its polling time is shown. The hourly chart and the comparison below use separate historical measurements.</Text>
    <Text style={styles.muted}>The source may report zero when a solar reading is unavailable. Zero alone does not confirm that the panels produced no power.</Text>
    {loading && !state ? <LoadingState label="Loading estimate details..." /> : null}
    <Card style={styles.card}>
      <TileHeader title="Weather calculation" onRefresh={() => void refresh()} loading={loading} />
      {!currentEstimate ? <Text style={styles.muted}>No weather estimate is available.</Text> : <>
        {state?.refreshFailed || state?.error ? <Text style={styles.warning}>This retained estimate is shown with its original time; it is not a fresh reading.</Text> : null}
        <DetailRow label="Central estimate" value={amount(currentEstimate.centralKw, "kW")} />
        <DetailRow label="Estimated range" value={`${amount(currentEstimate.lowerKw, "kW")} – ${amount(currentEstimate.upperKw, "kW")}`} />
        <DetailRow label="Power basis" value={powerBasis[currentEstimate.basis]} />
        <DetailRow label="Installed solar capacity" value={amount(currentEstimate.totalKwp, "kWp")} />
        <DetailRow label="Weather source" value={currentEstimate.observation.kind === 1 ? "Weather model" : "Satellite observation"} />
        <DetailRow label="Estimate time" value={momentCaption(currentEstimate.timestamp, timeZone)} />
        <DetailRow label="Calculated at" value={momentCaption(currentEstimate.calculatedAt, timeZone)} />
        <DetailRow label={currentEstimate.observation.kind === 1 ? "Model radiation time" : "Radiation observation"} value={momentCaption(currentEstimate.observation.timestamp, timeZone)} />
        <DetailRow label={currentEstimate.observation.kind === 1 ? "Model weather time" : "Weather observation"} value={currentEstimate.observation.weatherTimestamp ? momentCaption(currentEstimate.observation.weatherTimestamp, timeZone) : "Unavailable"} />
        <DetailRow label="Weather retrieved" value={currentEstimate.observation.retrievedAt ? momentCaption(currentEstimate.observation.retrievedAt, timeZone) : "Unavailable"} />
        {currentEstimate.weatherMissing ? <Text style={styles.warning}>Some weather inputs are missing. The estimate includes a calculated uncertainty range.</Text> : null}
      </>}
      {state?.lastSuccessAt ? <DetailRow label="Last successful weather refresh" value={momentCaption(state.lastSuccessAt, timeZone)} /> : null}
      <Text style={styles.muted}>An estimate represents solar power under the reported weather and installation capacity. It is separate from grid export and battery power.</Text>
      <Text style={styles.muted}>Refresh reloads the latest server results. Weather timestamps show when the underlying source was updated.</Text>
      {!isDemo ? <Pressable accessibilityRole="link" onPress={() => void Linking.openURL("https://open-meteo.com/en/docs")}><Text style={styles.link}>Open-Meteo weather · CC BY 4.0</Text></Pressable> : null}
    </Card>
    <Card style={styles.card}>
      <Text style={styles.title}>Time-aligned comparison</Text>
      <StatusPill label={verified ? comparisonStatus[state.comparison.status]! : "Comparison unavailable"} tone={verified ? "info" : "neutral"} />
      <ErrorBanner message={state?.error} />
      {actual ? <>
        <DetailRow label="Historical inverter power" value={amount(actual.powerKw, "kW")} />
        <DetailRow label="Historical inverter basis" value={powerBasis[actual.basis]} />
        <DetailRow label="inverter measurement time" value={momentCaption(actual.timestamp, timeZone)} />
      </> : <Text style={styles.muted}>No inverter measurement is available for this comparison.</Text>}
      {actual && matchedEstimate ? <>
        <DetailRow label="Estimate for comparison" value={amount(matchedEstimate.centralKw, "kW")} />
        <DetailRow label="Comparison range" value={`${amount(matchedEstimate.lowerKw, "kW")} – ${amount(matchedEstimate.upperKw, "kW")}`} />
        <DetailRow label="Comparison estimate time" value={momentCaption(matchedEstimate.timestamp, timeZone)} />
      </> : null}
      {verified ? <>
        <DetailRow label="inverter minus estimate" value={amount(state.comparison.deviationKw, "kW")} />
        <DetailRow label="Deviation" value={amount(state.comparison.deviationPercent, "%", 1)} />
      </> : <Text style={styles.warning}>{state?.comparison.reason ?? "A verified comparison is not available."}</Text>}
      {state?.refreshFailed ? <Text style={styles.warning}>Weather refresh failed; a verified comparison is unavailable.</Text> : null}
      <Text style={styles.muted}>The server checks measurement type, age and timestamp alignment. This historical inverter value can differ from the latest reading above. A percentage comparison may be unavailable at night or at very low power.</Text>
    </Card>
    <Text style={styles.muted}>Displayed time zone: {timeZone}.</Text>
  </Screen>;
}

function DetailRow({ label, value }: { label: string; value: string }) {
  return <View style={{ gap: 3 }}><Text style={styles.muted}>{label}</Text><Text style={styles.muted} selectable>{value}</Text></View>;
}
