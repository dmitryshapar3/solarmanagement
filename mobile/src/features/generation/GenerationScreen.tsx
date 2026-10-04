import { ReactNode, useCallback, useMemo, useRef, useState } from "react";
import { Linking, Pressable, ScrollView, StyleSheet, Text, View } from "react-native";
import { CompositeNavigationProp, NavigationProp, useNavigation } from "@react-navigation/native";
import { NativeStackNavigationProp } from "@react-navigation/native-stack";
import { AppButton, Card, ErrorBanner, Header, LoadingState, Screen, SegmentedControl } from "../../core/components";
import { InverterData, SolarEstimateState, SolarHistoryPeriod, SolarHistoryResult } from "../../core/api/types";
import { TileHeader } from "../../core/TileHeader";
import { useAuth } from "../../application/AuthContext";
import { RootStackParamList, RootTabsParamList } from "../../application/navigationTypes";
import { EnergyChart } from "../energy/EnergyChart";
import { addDays, amount, canSelectPreviousHistoryDay, dateCaption, known, momentCaption, tickCaption, validRange, zonedDate } from "../energy/chartPolicy";
import { energyStyles as styles, PeriodNavigation } from "../energy/EnergyControls";
import { useFocusedResource } from "../energy/useFocusedResource";
import { colors, spacing } from "../../core/theme";

export function GenerationScreen() {
  const { api } = useAuth();
  const navigation = useNavigation<CompositeNavigationProp<NavigationProp<RootTabsParamList>, NativeStackNavigationProp<RootStackParamList>>>();
  const inverter = useFocusedResource("generation-dashboard", useCallback((signal: AbortSignal, force: boolean) =>
    force ? api.refreshDashboard(signal) : api.getDashboard(signal), [api]));
  return <Screen>
    <Header title="Generation" subtitle="Weather estimates and measured solar power" />
    <GenerationPanel liveInverter={inverter.data?.inverter} inverterLoading={inverter.loading} inverterError={inverter.error}
      timeZoneId={inverter.data?.timeZoneId} onRefreshInverter={() => inverter.refresh(true)}
      onDetails={(period, date) => navigation.navigate("SolarEstimateDetails", { period, date })} />
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
  const actualKw = liveInverter && liveInverter.solarPowerValid !== false && Number.isFinite(liveInverter.solarProduction) && liveInverter.solarProduction >= 0 ? liveInverter.solarProduction / 1000 : null;
  return <>
    <View style={styles.metrics}>
      <View style={styles.metric}><Text style={styles.muted}>Latest reported inverter</Text><Text style={[styles.metricValue, styles.primaryValue]}>{amount(actualKw, "kW", 2)}</Text></View>
      <View style={styles.metric}><Text style={styles.muted}>Possible · weather estimate</Text><Text style={[styles.metricValue, styles.amberValue]}>{amount(sourceAvailable ? state.estimate?.centralKw : null, "kW", 2)}</Text></View>
    </View>
    {liveInverter ? <Text style={styles.muted}>Polled {momentCaption(liveInverter.timestamp, timeZoneId)} · {liveInverter.dataSource}</Text> : <Text style={styles.muted}>Waiting for the latest inverter reading.</Text>}
    {state?.estimate ? <Text style={styles.muted}>{state.estimate.observation.kind === 1 ? "Weather model" : "Satellite estimate"} for {momentCaption(state.estimate.timestamp, timeZoneId)}. The inverter reading and estimate may refer to different times.</Text> : <Text style={styles.muted}>Waiting for a power estimate.</Text>}
    {state?.error ? <Text style={styles.warning}>{state.error}</Text> : null}
    {state?.refreshFailed ? <Text style={styles.warning}>Weather refresh failed. Current inverter power remains separate from the unavailable estimate.</Text> : null}
  </>;
}

type GenerationPanelProps = {
  compact?: boolean;
  onDetails?: (period: SolarHistoryPeriod, date: string) => void;
  liveInverter?: InverterData | null;
  onRefreshInverter?: () => Promise<void>;
  inverterLoading?: boolean;
  inverterError?: string | null;
  timeZoneId?: string;
};

export function GenerationPanel({ compact = false, onDetails, liveInverter, onRefreshInverter, inverterLoading = false, inverterError, timeZoneId = "Europe/Warsaw" }: GenerationPanelProps) {
  const { api } = useAuth();
  const refreshPending = useRef(false);
  const estimate = useFocusedResource("solar-estimate", useCallback((signal: AbortSignal) => api.getSolarEstimate(signal), [api]));
  const loading = estimate.loading || inverterLoading;
  async function refreshSnapshot() {
    if (loading || refreshPending.current) return;
    refreshPending.current = true;
    try { await Promise.all([estimate.refresh(), onRefreshInverter?.()]); }
    finally { refreshPending.current = false; }
  }
  return <GenerationHistoryPanel compact={compact} onDetails={onDetails} snapshotLoading={loading} onRefreshSnapshot={refreshSnapshot}
    snapshot={(details) => <CurrentSolarSnapshot state={estimate.data} liveInverter={liveInverter} timeZoneId={timeZoneId}
      loading={loading} error={estimate.error ?? inverterError} onRefresh={() => void refreshSnapshot()} onDetails={details} />}
    compactContent={<><ErrorBanner message={estimate.error ?? inverterError} /><SolarSnapshotValues state={estimate.data} liveInverter={liveInverter} timeZoneId={timeZoneId} /></>} />;
}

type GenerationHistoryPanelProps = {
  compact?: boolean;
  initialPeriod?: SolarHistoryPeriod;
  initialDate?: string;
  onDetails?: (period: SolarHistoryPeriod, date: string) => void;
  snapshot?: (details?: () => void) => ReactNode;
  compactContent?: ReactNode;
  snapshotLoading?: boolean;
  onRefreshSnapshot?: () => Promise<void>;
  detailed?: boolean;
};

// This component owns the selected history window. Its chart and table always read the same response.
export function GenerationHistoryPanel({ compact = false, initialPeriod = "Today", initialDate, onDetails, snapshot, compactContent, snapshotLoading = false, onRefreshSnapshot, detailed = false }: GenerationHistoryPanelProps) {
  const { api, isDemo } = useAuth();
  const [period, setPeriod] = useState<SolarHistoryPeriod>(initialPeriod);
  const [date, setDate] = useState<string | undefined>(initialDate);
  const resource = useFocusedResource(`solar-history:${period}:${date ?? "today"}`,
    useCallback((signal: AbortSignal) => api.getSolarHistory(period, date, signal), [api, period, date]));
  const data = resource.data;
  const loading = resource.loading || snapshotLoading;
  const today = data?.today ?? zonedDate(new Date());
  const selected = data?.selectedDate ?? date ?? today;
  const details = onDetails ? () => onDetails(period, selected) : undefined;
  async function refresh() {
    if (loading) return;
    await Promise.all([resource.refresh(), onRefreshSnapshot?.()]);
  }
  const points = useMemo(() => data?.points.map((point) => ({
    timestamp: point.timestamp, label: tickCaption(point.timestamp, data.timeZoneId, period === "Today" ? "Day" : "Month"),
    possible: point.possible, actual: point.actualKw,
    description: `${momentCaption(point.timestamp, data.timeZoneId)}\nPossible ${validRange(point.possible) ? `${point.possible.lowerKw.toFixed(2)}–${point.possible.upperKw.toFixed(2)} kW` : "— kW"} · Actual ${amount(point.actualKw, "kW", 2)}`
  })) ?? [], [data, period]);
  return <>
    {!compact ? snapshot?.(details) : null}
    <Card style={styles.card}>
      <TileHeader title={compact ? "Possible generation" : "Generation history"} loading={loading} onRefresh={() => void refresh()} onDetails={details} />
      {compact ? compactContent : null}
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
      <Text style={styles.muted}>Tap the chart or use Previous / Next interval to inspect exact values. These are hourly average power; missing readings remain gaps.</Text>
      {data && detailed ? <GenerationHistoryTable key={`${data.start}:${data.end}`} data={data} /> : null}
      {!compact ? <View style={styles.divider}><Text style={styles.muted}>Possible power is calculated from weather data. The shaded band shows the estimate range. It is separate from the latest inverter reading.</Text>{!isDemo ? <Pressable accessibilityRole="link" onPress={() => void Linking.openURL("https://open-meteo.com/en/docs")}><Text style={styles.link}>Open-Meteo weather · CC BY 4.0</Text></Pressable> : null}</View> : null}
    </Card>
  </>;
}

function GenerationHistoryTable({ data }: { data: SolarHistoryResult }) {
  const [page, setPage] = useState(0);
  const pageSize = 24;
  const pages = Math.max(1, Math.ceil(data.points.length / pageSize));
  const currentPage = Math.min(page, pages - 1);
  const rows = data.points.slice(currentPage * pageSize, (currentPage + 1) * pageSize);
  const actualHours = data.points.filter((point) => known(point.actualKw)).length;
  const possibleHours = data.points.filter((point) => validRange(point.possible)).length;
  return <View style={styles.divider}>
    <Text style={styles.title}>Hourly generation table</Text>
    <Text style={styles.muted}>Actual: {actualHours} of {data.points.length} hours available · Possible: {possibleHours} of {data.points.length}. Availability counts returned intervals, including any future hours.</Text>
    <Text style={styles.muted}>{momentCaption(data.start, data.timeZoneId)} – {momentCaption(data.end, data.timeZoneId)} · {data.timeZoneId}. End time is exclusive. Swipe the table horizontally for all columns.</Text>
    <ScrollView horizontal nestedScrollEnabled>
      <View>
        <View style={tableStyles.row}>{["Hour starting", "Actual kW", "Possible low kW", "Possible high kW", "Availability"].map((label, index) => <Text key={label} style={[tableStyles.cell, tableStyles.header, index === 0 && tableStyles.time]}>{label}</Text>)}</View>
        {rows.map((point) => <View key={point.timestamp} style={tableStyles.row}>
          <Text style={[tableStyles.cell, tableStyles.time]}>{momentCaption(point.timestamp, data.timeZoneId)}</Text>
          <Text selectable style={tableStyles.cell}>{amount(point.actualKw, "", 2).trim()}</Text>
          <Text selectable style={tableStyles.cell}>{amount(validRange(point.possible) ? point.possible.lowerKw : null, "", 2).trim()}</Text>
          <Text selectable style={tableStyles.cell}>{amount(validRange(point.possible) ? point.possible.upperKw : null, "", 2).trim()}</Text>
          <Text style={tableStyles.cell}>{known(point.actualKw) && validRange(point.possible) ? "Both" : known(point.actualKw) ? "Actual only" : validRange(point.possible) ? "Possible only" : "Missing"}</Text>
        </View>)}
      </View>
    </ScrollView>
    {!rows.length ? <Text style={styles.muted}>No hourly intervals were returned.</Text> : null}
    <View style={tableStyles.pager}>
      <Pressable accessibilityRole="button" accessibilityLabel="Previous table page" disabled={currentPage === 0} onPress={() => setPage(currentPage - 1)}><Text style={[styles.link, currentPage === 0 && tableStyles.disabled]}>Previous rows</Text></Pressable>
      <Text style={styles.muted}>Page {currentPage + 1} of {pages}</Text>
      <Pressable accessibilityRole="button" accessibilityLabel="Next table page" disabled={currentPage === pages - 1} onPress={() => setPage(currentPage + 1)}><Text style={[styles.link, currentPage === pages - 1 && tableStyles.disabled]}>Next rows</Text></Pressable>
    </View>
    <Text style={styles.muted}>A dash means unavailable; measured zero remains 0.00. Hour timestamps include the UTC offset to distinguish repeated daylight-saving hours. Power is in kW, not energy in kWh.</Text>
  </View>;
}

const tableStyles = StyleSheet.create({
  row: { flexDirection: "row", borderBottomWidth: StyleSheet.hairlineWidth, borderBottomColor: colors.border },
  cell: { width: 116, paddingVertical: spacing.sm, paddingHorizontal: spacing.sm, color: colors.text, fontSize: 12, fontVariant: ["tabular-nums"] },
  time: { width: 190 }, header: { color: colors.muted, fontWeight: "700" },
  pager: { flexDirection: "row", justifyContent: "space-between", alignItems: "center", gap: spacing.sm }, disabled: { opacity: .3 }
});
