import { useLanguage } from "../../application/LanguageContext";
import { useCallback } from "react";
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
import { useGroupedRefresh } from "../energy/useGroupedRefresh";
import { CurrentSolarSnapshot, GenerationHistoryPanel } from "./GenerationScreen";

const powerBasis: Record<SolarPowerBasis, string> = { 0: "PV DC generation", 1: "Inverter AC power", 2: "Grid export" };
const comparisonStatus = ["Comparison unavailable", "Within estimate range", "Below estimate range", "Above estimate range"];

export function SolarEstimateDetailsScreen() {
  const { t } = useLanguage();
  const { api, isDemo } = useAuth();
  const route = useRoute<RouteProp<RootStackParamList, "SolarEstimateDetails">>();
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
  const refresh = useGroupedRefresh(loading, [() => estimate.refresh(), () => inverter.refresh(true)]);
  return <Screen refreshing={loading} onRefresh={() => void refresh()}>
    <Header title={t("Solar estimate details")} subtitle={t("Hourly actual and possible power, weather calculation and measurement comparison")} />
    <GenerationHistoryPanel initialPeriod={route.params?.period} initialDate={route.params?.date} detailed
      onRefreshSnapshot={refresh} snapshotLoading={loading} />
    <CurrentSolarSnapshot state={state} liveInverter={inverter.data?.inverter} timeZoneId={timeZone}
      error={estimate.error ?? inverter.error} loading={loading} onRefresh={() => void refresh()} />
    <Text style={styles.muted}>{t("Polling time may differ from the source measurement time. The hourly chart and comparison use separate saved measurements.")}</Text>
    <Text style={styles.muted}>{t("Unavailable measurements are shown with a dash. A valid measured zero is shown as 0.00.")}</Text>
    {loading && !state ? <LoadingState label={t("Loading estimate details...")} /> : null}
    <Card style={styles.card}>
      <TileHeader title={t("Weather calculation")} onRefresh={() => void refresh()} loading={loading} />
      {!currentEstimate ? <Text style={styles.muted}>{t("No weather estimate is available.")}</Text> : <>
        {state?.refreshFailed || state?.error ? <Text style={styles.warning}>{t("This retained estimate is shown with its original time; it is not a fresh reading.")}</Text> : null}
        <DetailRow label={t("Central estimate")} value={amount(currentEstimate.centralKw, "kW")} />
        <DetailRow label={t("Estimated range")} value={`${amount(currentEstimate.lowerKw, "kW")} – ${amount(currentEstimate.upperKw, "kW")}`} />
        <DetailRow label={t("Power basis")} value={powerBasis[currentEstimate.basis]} />
        <DetailRow label={t("Installed solar capacity")} value={amount(currentEstimate.totalKwp, "kWp")} />
        <DetailRow label={t("Weather source")} value={currentEstimate.observation.kind === 1 ? t("Weather model") : t("Satellite observation")} />
        <DetailRow label={t("Estimate time")} value={momentCaption(currentEstimate.timestamp, timeZone)} />
        <DetailRow label={t("Calculated at")} value={momentCaption(currentEstimate.calculatedAt, timeZone)} />
        <DetailRow label={currentEstimate.observation.kind === 1 ? t("Model radiation time") : t("Radiation observation")} value={momentCaption(currentEstimate.observation.timestamp, timeZone)} />
        <DetailRow label={currentEstimate.observation.kind === 1 ? t("Model weather time") : t("Weather observation")} value={currentEstimate.observation.weatherTimestamp ? momentCaption(currentEstimate.observation.weatherTimestamp, timeZone) : t("Unavailable")} />
        <DetailRow label={t("Weather retrieved")} value={currentEstimate.observation.retrievedAt ? momentCaption(currentEstimate.observation.retrievedAt, timeZone) : t("Unavailable")} />
        {currentEstimate.weatherMissing ? <Text style={styles.warning}>{t("Some weather inputs are missing. The estimate includes a calculated uncertainty range.")}</Text> : null}
      </>}
      {state?.lastSuccessAt ? <DetailRow label={t("Last successful weather refresh")} value={momentCaption(state.lastSuccessAt, timeZone)} /> : null}
      <Text style={styles.muted}>{t("An estimate represents solar power under the reported weather and installation capacity. It is separate from grid export and battery power.")}</Text>
      <Text style={styles.muted}>{t("Refresh reloads the latest server results. Weather timestamps show when the underlying source was updated.")}</Text>
      {!isDemo ? <Pressable accessibilityRole="link" onPress={() => void Linking.openURL("https://open-meteo.com/en/docs")}><Text style={styles.link}>{t("Open-Meteo weather · CC BY 4.0")}</Text></Pressable> : null}
    </Card>
    <Card style={styles.card}>
      <Text style={styles.title}>{t("Time-aligned comparison")}</Text>
      <StatusPill label={verified ? comparisonStatus[state.comparison.status]! : t("Comparison unavailable")} tone={verified ? "info" : "neutral"} />
      <ErrorBanner message={state?.error} />
      {actual ? <>
        <DetailRow label={t("Historical inverter power")} value={amount(actual.powerKw, "kW")} />
        <DetailRow label={t("Historical inverter basis")} value={powerBasis[actual.basis]} />
        <DetailRow label={t("inverter measurement time")} value={momentCaption(actual.timestamp, timeZone)} />
      </> : <Text style={styles.muted}>{t("No inverter measurement is available for this comparison.")}</Text>}
      {actual && matchedEstimate ? <>
        <DetailRow label={t("Estimate for comparison")} value={amount(matchedEstimate.centralKw, "kW")} />
        <DetailRow label={t("Comparison range")} value={`${amount(matchedEstimate.lowerKw, "kW")} – ${amount(matchedEstimate.upperKw, "kW")}`} />
        <DetailRow label={t("Comparison estimate time")} value={momentCaption(matchedEstimate.timestamp, timeZone)} />
      </> : null}
      {verified ? <>
        <DetailRow label={t("inverter minus estimate")} value={amount(state.comparison.deviationKw, "kW")} />
        <DetailRow label={t("Deviation")} value={amount(state.comparison.deviationPercent, "%", 1)} />
      </> : <Text style={styles.warning}>{t(state?.comparison.reason ?? "A verified comparison is not available.")}</Text>}
      {state?.refreshFailed ? <Text style={styles.warning}>{t("Weather refresh failed; a verified comparison is unavailable.")}</Text> : null}
      <Text style={styles.muted}>{t("The server checks measurement type, age and timestamp alignment. This historical inverter value can differ from the latest reading above. A percentage comparison may be unavailable at night or at very low power.")}</Text>
    </Card>
    <Text style={styles.muted}>{t("Displayed time zone: {0}.", timeZone)}</Text>
  </Screen>;
}

function DetailRow({ label, value }: { label: string; value: string }) {
  const { t } = useLanguage();
  return <View style={{ gap: 3 }}><Text style={styles.muted}>{t(label)}</Text><Text style={styles.muted} selectable>{t(value)}</Text></View>;
}
