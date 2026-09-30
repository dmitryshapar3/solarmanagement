import { useCallback, useMemo, useState } from "react";
import { Pressable, Text, View } from "react-native";
import { RefreshCcw } from "lucide-react-native";
import { AppButton, Card, ErrorBanner, Header, LoadingState, Screen, SegmentedControl, StatusPill } from "../../core/components";
import { useAuth } from "../../application/AuthContext";
import { EnergyChart } from "../energy/EnergyChart";
import { amount, dateCaption, momentCaption, movePeriod, periodAnchor, salesPointValues, tickCaption, zonedDate } from "../energy/chartPolicy";
import { energyStyles as styles, PeriodNavigation } from "../energy/EnergyControls";
import { useFocusedResource } from "../energy/useFocusedResource";

type SalesPeriod = "Day" | "Month" | "Year";
type Metric = "energy" | "value";

export function SalesScreen() {
  return <Screen><Header title="Sales" subtitle="Measured grid export and estimated energy value" /><SalesPanel /></Screen>;
}

export function SalesPanel({ compact = false, onDetails }: { compact?: boolean; onDetails?: () => void }) {
  const { api } = useAuth();
  const [period, setPeriod] = useState<SalesPeriod>("Day");
  const [date, setDate] = useState<string>();
  const [metric, setMetric] = useState<Metric>("energy");
  const resource = useFocusedResource(`sales:${period}:${date ?? "today"}`,
    useCallback((signal: AbortSignal) => api.getSales(period, date ?? zonedDate(new Date()), signal), [api, period, date]));
  const data = resource.data;
  const today = data?.today ?? zonedDate(new Date());
  const selected = data?.request.date ?? date ?? today;
  const unit = metric === "energy" ? "kWh" : "PLN";
  const points = useMemo(() => data?.buckets.map((bucket) => {
    const { current, completed, provisional } = salesPointValues(bucket, data.currentHour, metric);
    return { timestamp: bucket.start, label: tickCaption(bucket.start, data.timeZoneId, period), completed, provisional,
      description: `${momentCaption(bucket.start, data.timeZoneId)} – ${momentCaption(bucket.end, data.timeZoneId)}\nCompleted: ${amount(completed, unit)}${current ? `\nCurrent hour · in progress: ${amount(provisional, unit)}${current.observedThrough ? ` · measured through ${momentCaption(current.observedThrough, data.timeZoneId)}` : " · awaiting readings"}` : ""}${bucket.observedHours < bucket.expectedHours || bucket.valuedHours < bucket.observedHours ? "\nPartial interval" : ""}` };
  }) ?? [], [data, metric, period, unit]);
  const hasElapsed = (data?.expectedHours ?? 0) > 0;
  return <Card style={styles.card}>
    <View style={styles.heading}><Text style={styles.title}>Electricity sales</Text>{compact ? <Pressable accessibilityRole="button" onPress={onDetails}><Text style={styles.link}>View details</Text></Pressable> : <AppButton label="Refresh" icon={RefreshCcw} compact variant="ghost" loading={resource.loading} onPress={() => void resource.refresh()} />}</View>
    <StatusPill label="Deye estimate" tone="info" />
    {!compact ? <>
      <SegmentedControl options={[{ label: "Day", value: "Day" }, { label: "Month", value: "Month" }, { label: "Year", value: "Year" }]} value={period} onChange={setPeriod} />
      <PeriodNavigation caption={dateCaption(selected, period)} previous={periodAnchor(selected, period) > "2000-01-01"} next={periodAnchor(selected, period) < periodAnchor(today, period)}
        onPrevious={() => setDate(movePeriod(selected, period, -1))} onNext={() => setDate(movePeriod(selected, period, 1))} onToday={() => { setDate(undefined); setPeriod("Day"); }} />
    </> : <Text style={styles.muted}>{dateCaption(selected)}</Text>}
    <ErrorBanner message={resource.error} />
    {resource.error ? <AppButton label="Retry" compact variant="ghost" loading={resource.loading} onPress={() => void resource.refresh()} /> : null}
    <View style={styles.metrics}>
      <View style={styles.metric}><Text style={styles.muted}>{compact && selected === today && selected === zonedDate(new Date(), data?.timeZoneId) ? "Exported today" : "Exported to grid"}</Text><Text style={[styles.metricValue, styles.primaryValue]}>{amount(hasElapsed ? data?.exportKwh : null, "kWh")}</Text></View>
      <View style={styles.metric}><Text style={styles.muted}>Energy value</Text><Text style={[styles.metricValue, styles.amberValue]}>{amount(hasElapsed ? data?.energyValuePln : null, "PLN")}</Text></View>
    </View>
    {!compact ? <View style={styles.metric}><Text style={styles.muted}>Estimated deposit</Text><Text style={styles.metricValue}>{amount(hasElapsed ? data?.estimatedDepositPln : null, "PLN")}</Text></View> : null}
    <Text style={styles.muted}>Totals cover completed hours. Current hour is provisional.</Text>
    {data?.updatedAt ? <Text style={styles.muted}>Updated {momentCaption(data.updatedAt, data.timeZoneId)} · refreshes every 5 minutes</Text> : null}
    {data?.dataError ? <Text style={styles.warning}>{data.dataError}</Text> : null}
    {data?.priceError ? <Text style={styles.warning}>{data.priceError}</Text> : null}
    {data?.isPartial ? <Text style={styles.warning}>Partial data · totals for available hours</Text> : null}
    {data && !hasElapsed && !data.currentHour && !data.dataError ? <Text style={styles.muted}>{movePeriod(selected, period, 1) <= data.contractStartDate ? `This period is before the contract start date: ${dateCaption(data.contractStartDate)}.` : "There are no completed hours in this period yet."}</Text> : null}
    <SegmentedControl options={[{ label: "Energy", value: "energy" }, { label: "Value", value: "value" }]} value={metric} onChange={setMetric} />
    {resource.loading && !data ? <LoadingState label="Loading sales..." /> : <EnergyChart key={`${data?.start}:${data?.end}`} points={points} mode="sales" unit={unit} />}
    {data?.currentHour ? <View style={styles.provisional}>
      <Text style={[styles.muted, styles.primaryValue]}>Current hour · in progress</Text>
      <Text style={styles.muted}>{amount(data.currentHour.exportKwh, "kWh")} · {amount(data.currentHour.energyValuePln, "PLN")}</Text>
      <Text style={styles.muted}>{data.currentHour.observedThrough ? `Measured through ${momentCaption(data.currentHour.observedThrough, data.timeZoneId)}` : "Awaiting current-hour readings."}</Text>
      {data.currentHour.exportKwh !== null && data.currentHour.energyValuePln === null ? <Text style={styles.warning}>Awaiting current-hour prices.</Text> : null}
      <Text style={styles.muted}>Measured intervals only; no projection to the end of the hour.</Text>
    </View> : null}
    {!compact && data ? <View style={styles.divider}>
      <Text style={styles.muted}>TAURON contract from {dateCaption(data.contractStartDate)}. After hourly netting of imports and exports: {amount(hasElapsed ? data.creditedExportKwh : null, "kWh")}.</Text>
      <Text style={styles.muted}>Energy value uses RCE prices under the contract terms. Estimated monthly deposit credit includes the 1.23 multiplier. It is not a bank payout or deposit balance.</Text>
      <Text style={styles.muted}>Final settlement uses the OSD billing meter. Missing readings and prices are not zero. Settlement time zone: {data.timeZoneId}.</Text>
    </View> : null}
  </Card>;
}
