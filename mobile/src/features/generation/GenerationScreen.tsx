import { useCallback, useMemo, useRef, useState } from "react";
import { Linking, Pressable, Text, View } from "react-native";
import { CompositeNavigationProp, NavigationProp, useNavigation } from "@react-navigation/native";
import { NativeStackNavigationProp } from "@react-navigation/native-stack";
import { AppButton, Card, ErrorBanner, Header, LoadingState, Screen, SegmentedControl } from "../../core/components";
import { InverterData, SolarEstimateState, SolarHistoryPeriod } from "../../core/api/types";
import { TileHeader } from "../../core/TileHeader";
import { useAuth } from "../../application/AuthContext";
import { RootStackParamList, RootTabsParamList } from "../../application/navigationTypes";
import { EnergyChart } from "../energy/EnergyChart";
import { addDays, amount, canSelectPreviousHistoryDay, dateCaption, momentCaption, tickCaption, zonedDate } from "../energy/chartPolicy";
import { energyStyles as styles, PeriodNavigation } from "../energy/EnergyControls";
import { useFocusedResource } from "../energy/useFocusedResource";

export function GenerationScreen() {
  const { api } = useAuth();
  const navigation = useNavigation<CompositeNavigationProp<NavigationProp<RootTabsParamList>, NativeStackNavigationProp<RootStackParamList>>>();
  const inverter = useFocusedResource("generation-dashboard", useCallback((signal: AbortSignal, force: boolean) =>
    force ? api.refreshDashboard(signal) : api.getDashboard(signal), [api]));
  return <Screen>
    <Header title="Generation" subtitle="Weather estimates and measured solar power" />
    <GenerationPanel liveInverter={inverter.data?.inverter} inverterLoading={inverter.loading} inverterError={inverter.error}
      timeZoneId={inverter.data?.timeZoneId} onRefreshInverter={() => inverter.refresh(true)}
      onDetails={() => navigation.navigate("SolarEstimateDetails")} />
  </Screen>;
}

type SolarSnapshotProps = {
  state: SolarEstimateState | null;
  liveInverter?: InverterData | null;
  timeZoneId?: string;
  error?: string | null;
  loading?: boolean;
  onRefresh: () => void;
  onDetails?: () => void;
};

export function CurrentSolarSnapshot({ state, liveInverter, timeZoneId = "Europe/Warsaw", error, loading, onRefresh, onDetails }: SolarSnapshotProps) {
  return <Card style={styles.card}>
    <TileHeader title="Latest solar snapshot" loading={loading} onRefresh={onRefresh} onDetails={onDetails} />
    <ErrorBanner message={error} />
    <SolarSnapshotValues state={state} liveInverter={liveInverter} timeZoneId={timeZoneId} />
  </Card>;
}

function SolarSnapshotValues({ state, liveInverter, timeZoneId }: Pick<SolarSnapshotProps, "state" | "liveInverter"> & { timeZoneId: string }) {
  const sourceAvailable = state && !state.error && !state.refreshFailed;
  const actualKw = liveInverter && Number.isFinite(liveInverter.solarProduction) && liveInverter.solarProduction >= 0 ? liveInverter.solarProduction / 1000 : null;
  return <>
    <View style={styles.metrics}>
      <View style={styles.metric}><Text style={styles.muted}>Latest reported Deye</Text><Text style={[styles.metricValue, styles.primaryValue]}>{amount(actualKw, "kW", 2)}</Text></View>
      <View style={styles.metric}><Text style={styles.muted}>Possible · weather estimate</Text><Text style={[styles.metricValue, styles.amberValue]}>{amount(sourceAvailable ? state.estimate?.centralKw : null, "kW", 2)}</Text></View>
    </View>
    {liveInverter ? <Text style={styles.muted}>Polled {momentCaption(liveInverter.timestamp, timeZoneId)} · {liveInverter.dataSource}</Text> : <Text style={styles.muted}>Waiting for the latest Deye reading.</Text>}
    {state?.estimate ? <Text style={styles.muted}>{state.estimate.observation.kind === 1 ? "Weather model" : "Satellite estimate"} for {momentCaption(state.estimate.timestamp, timeZoneId)}. The Deye reading and estimate may refer to different times.</Text> : <Text style={styles.muted}>Waiting for a power estimate.</Text>}
    {state?.error ? <Text style={styles.warning}>{state.error}</Text> : null}
    {state?.refreshFailed ? <Text style={styles.warning}>Weather refresh failed. Current Deye power remains separate from the unavailable estimate.</Text> : null}
  </>;
}

type GenerationPanelProps = {
  compact?: boolean;
  onDetails?: () => void;
  liveInverter?: InverterData | null;
  onRefreshInverter?: () => Promise<void>;
  inverterLoading?: boolean;
  inverterError?: string | null;
  timeZoneId?: string;
};

export function GenerationPanel({ compact = false, onDetails, liveInverter, onRefreshInverter, inverterLoading = false, inverterError, timeZoneId }: GenerationPanelProps) {
  const { api } = useAuth();
  const refreshPending = useRef(false);
  const [period, setPeriod] = useState<SolarHistoryPeriod>("Today");
  const [date, setDate] = useState<string>();
  const estimate = useFocusedResource("solar-estimate", useCallback((signal: AbortSignal) => api.getSolarEstimate(signal), [api]));
  const resource = useFocusedResource(`solar-history:${period}:${date ?? "today"}`,
    useCallback((signal: AbortSignal) => api.getSolarHistory(period, date, signal), [api, period, date]));
  const data = resource.data;
  const loading = resource.loading || estimate.loading || inverterLoading;
  const zone = timeZoneId ?? data?.timeZoneId ?? "Europe/Warsaw";
  async function refresh() {
    if (loading || refreshPending.current) return;
    refreshPending.current = true;
    try { await Promise.all([resource.refresh(), estimate.refresh(), onRefreshInverter?.()]); }
    finally { refreshPending.current = false; }
  }
  const today = data?.today ?? zonedDate(new Date());
  const selected = data?.selectedDate ?? date ?? today;
  const points = useMemo(() => data?.points.map((point) => ({
    timestamp: point.timestamp, label: tickCaption(point.timestamp, data.timeZoneId, period === "Today" ? "Day" : "Month"),
    possible: point.possible, actual: point.actualKw,
    description: `${momentCaption(point.timestamp, data.timeZoneId)}\nPossible ${point.possible ? `${point.possible.lowerKw.toFixed(1)}–${point.possible.upperKw.toFixed(1)} kW` : "— kW"} · Actual ${amount(point.actualKw, "kW", 1)}`
  })) ?? [], [data, period]);
  return <>
    {!compact ? <CurrentSolarSnapshot state={estimate.data} liveInverter={liveInverter} timeZoneId={zone}
      loading={loading} error={estimate.error ?? inverterError} onRefresh={() => void refresh()} onDetails={onDetails} /> : null}
    <Card style={styles.card}>
    <TileHeader title={compact ? "Possible generation" : "Generation history"} loading={loading} onRefresh={() => void refresh()} onDetails={onDetails} />
    {compact ? <><ErrorBanner message={estimate.error ?? inverterError} /><SolarSnapshotValues state={estimate.data} liveInverter={liveInverter} timeZoneId={zone} /></> : null}
    {!compact ? <>
      <SegmentedControl options={[{ label: "Day", value: "Today" }, { label: "7 days", value: "Week" }, { label: "30 days", value: "Month" }]} value={period} onChange={setPeriod} />
      <PeriodNavigation caption={dateCaption(selected)} previous={canSelectPreviousHistoryDay(selected, today)} next={selected < today}
        onPrevious={() => setDate(addDays(selected, -1))} onNext={() => setDate(addDays(selected, 1))} onToday={() => { setDate(undefined); setPeriod("Today"); }} />
    </> : <Text style={styles.muted}>{dateCaption(selected)} · hourly average power</Text>}
    <ErrorBanner message={resource.error} />
    {resource.error ? <AppButton label="Retry" compact variant="ghost" loading={loading} onPress={() => void refresh()} /> : null}
    {data?.weatherError ? <Text style={styles.warning}>{data.weatherError}</Text> : null}
    {data?.actualError ? <Text style={styles.warning}>{data.actualError}</Text> : null}
    <View style={styles.legend}><Text style={[styles.muted, styles.amberValue]}>■ Possible · range</Text><Text style={[styles.muted, styles.primaryValue]}>━ Actual</Text></View>
    {resource.loading && !data ? <LoadingState label="Loading generation..." /> : <EnergyChart key={`${data?.start}:${data?.end}`} points={points} mode="generation" unit="kW" />}
    <Text style={styles.muted}>The chart shows hourly average power. The current Deye reading above is a separate measurement; missing readings remain gaps.</Text>
    {!compact ? <View style={styles.divider}><Text style={styles.muted}>Possible power is calculated from weather data. The shaded band shows the estimate range.</Text><Pressable accessibilityRole="link" onPress={() => void Linking.openURL("https://open-meteo.com/en/docs")}><Text style={styles.link}>Open-Meteo weather · CC BY 4.0</Text></Pressable></View> : null}
    </Card>
  </>;
}
