import { useCallback, useMemo, useState } from "react";
import { Linking, Pressable, Text, View } from "react-native";
import { RefreshCcw } from "lucide-react-native";
import { AppButton, Card, ErrorBanner, Header, LoadingState, Screen, SegmentedControl } from "../../core/components";
import { SolarHistoryPeriod } from "../../core/api/types";
import { useAuth } from "../../application/AuthContext";
import { EnergyChart } from "../energy/EnergyChart";
import { addDays, amount, canSelectPreviousHistoryDay, dateCaption, momentCaption, tickCaption, zonedDate } from "../energy/chartPolicy";
import { energyStyles as styles, PeriodNavigation } from "../energy/EnergyControls";
import { useFocusedResource } from "../energy/useFocusedResource";

export function GenerationScreen() {
  return <Screen><Header title="Generation" subtitle="Weather estimates and measured solar power" /><CurrentGeneration /><GenerationPanel /></Screen>;
}

function CurrentGeneration() {
  const { api } = useAuth();
  const resource = useFocusedResource("solar-estimate", useCallback((signal: AbortSignal) => api.getSolarEstimate(signal), [api]));
  const state = resource.data;
  const sourceAvailable = state && !state.error && !state.refreshFailed;
  const actual = sourceAvailable && state.comparison.status !== 0 ? state.comparison.actual : null;
  return <Card style={styles.card}>
    <View style={styles.heading}><Text style={styles.title}>Latest solar snapshot</Text><AppButton label="Refresh" icon={RefreshCcw} variant="ghost" compact loading={resource.loading} onPress={() => void resource.refresh()} /></View>
    <ErrorBanner message={resource.error} />
    <View style={styles.metrics}>
      <View style={styles.metric}><Text style={styles.muted}>Possible</Text><Text style={[styles.metricValue, styles.amberValue]}>{amount(sourceAvailable ? state.estimate?.centralKw : null, "kW", 1)}</Text></View>
      <View style={styles.metric}><Text style={styles.muted}>Verified Deye reading</Text><Text style={[styles.metricValue, styles.primaryValue]}>{amount(actual?.powerKw, "kW", 1)}</Text></View>
    </View>
    {state?.estimate ? <Text style={styles.muted}>{state.estimate.observation.kind === 1 ? "Weather model" : "Satellite estimate"} · captured for {momentCaption(state.estimate.timestamp, "Europe/Warsaw")}</Text> : <Text style={styles.muted}>Waiting for a power estimate.</Text>}
    {actual ? <Text style={styles.muted}>Deye measured {momentCaption(actual.timestamp, "Europe/Warsaw")}. Readings may refer to a different time than the latest estimate.</Text> : <Text style={styles.muted}>{sourceAvailable ? state.comparison.reason ?? "A verified Deye comparison is not available." : "Power values are unavailable until the source refresh succeeds."}</Text>}
    {state?.error ? <Text style={styles.warning}>{state.error}</Text> : null}
    {state?.refreshFailed ? <Text style={styles.warning}>Weather refresh failed. The timestamp identifies the retained estimate.</Text> : null}
  </Card>;
}

export function GenerationPanel({ compact = false, onDetails }: { compact?: boolean; onDetails?: () => void }) {
  const { api } = useAuth();
  const [period, setPeriod] = useState<SolarHistoryPeriod>("Today");
  const [date, setDate] = useState<string>();
  const resource = useFocusedResource(`solar-history:${period}:${date ?? "today"}`,
    useCallback((signal: AbortSignal) => api.getSolarHistory(period, date, signal), [api, period, date]));
  const data = resource.data;
  const today = data?.today ?? zonedDate(new Date());
  const selected = data?.selectedDate ?? date ?? today;
  const points = useMemo(() => data?.points.map((point) => ({
    timestamp: point.timestamp, label: tickCaption(point.timestamp, data.timeZoneId, period === "Today" ? "Day" : "Month"),
    possible: point.possible, actual: point.actualKw,
    description: `${momentCaption(point.timestamp, data.timeZoneId)}\nPossible ${point.possible ? `${point.possible.lowerKw.toFixed(1)}–${point.possible.upperKw.toFixed(1)} kW` : "— kW"} · Actual ${amount(point.actualKw, "kW", 1)}`
  })) ?? [], [data, period]);
  return <Card style={styles.card}>
    <View style={styles.heading}><Text style={styles.title}>Solar generation</Text>{compact ? <Pressable accessibilityRole="button" onPress={onDetails}><Text style={styles.link}>View details</Text></Pressable> : <AppButton label="Refresh" icon={RefreshCcw} compact variant="ghost" loading={resource.loading} onPress={() => void resource.refresh()} />}</View>
    {!compact ? <>
      <SegmentedControl options={[{ label: "Day", value: "Today" }, { label: "7 days", value: "Week" }, { label: "30 days", value: "Month" }]} value={period} onChange={setPeriod} />
      <PeriodNavigation caption={dateCaption(selected)} previous={canSelectPreviousHistoryDay(selected, today)} next={selected < today}
        onPrevious={() => setDate(addDays(selected, -1))} onNext={() => setDate(addDays(selected, 1))} onToday={() => { setDate(undefined); setPeriod("Today"); }} />
    </> : <Text style={styles.muted}>{dateCaption(selected)} · hourly average power</Text>}
    <ErrorBanner message={resource.error} />
    {resource.error ? <AppButton label="Retry" compact variant="ghost" loading={resource.loading} onPress={() => void resource.refresh()} /> : null}
    {data?.weatherError ? <Text style={styles.warning}>{data.weatherError}</Text> : null}
    {data?.actualError ? <Text style={styles.warning}>{data.actualError}</Text> : null}
    <View style={styles.legend}><Text style={[styles.muted, styles.amberValue]}>■ Possible · range</Text><Text style={[styles.muted, styles.primaryValue]}>━ Actual</Text></View>
    {resource.loading && !data ? <LoadingState label="Loading generation..." /> : <EnergyChart key={`${data?.start}:${data?.end}`} points={points} mode="generation" unit="kW" />}
    {!compact ? <View style={styles.divider}><Text style={styles.muted}>Possible power is calculated from weather data. Actual power is the hourly mean of Deye PV readings. Missing readings remain gaps.</Text><Pressable accessibilityRole="link" onPress={() => void Linking.openURL("https://open-meteo.com/en/docs")}><Text style={styles.link}>Open-Meteo weather · CC BY 4.0</Text></Pressable></View> : null}
  </Card>;
}
