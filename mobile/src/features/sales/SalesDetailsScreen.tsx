import { useCallback, useMemo } from "react";
import { StyleSheet, Text, View } from "react-native";
import { RouteProp, useRoute } from "@react-navigation/native";
import { useAuth } from "../../application/AuthContext";
import { RootStackParamList } from "../../application/navigationTypes";
import { Card, ErrorBanner, Header, LoadingState, Screen, StatusPill } from "../../core/components";
import { TileHeader } from "../../core/TileHeader";
import { colors, spacing, typography } from "../../core/theme";
import { amount, dateCaption, momentCaption, zonedDate } from "../energy/chartPolicy";
import { useFocusedResource } from "../energy/useFocusedResource";

export function SalesDetailsScreen() {
  const { api } = useAuth();
  const route = useRoute<RouteProp<RootStackParamList, "SalesDetails">>();
  const period = route.params?.period ?? "Day";
  const date = useMemo(() => route.params?.date ?? zonedDate(new Date()), [route.params?.date]);
  const resource = useFocusedResource(`sales-details:${period}:${date}`,
    useCallback((signal: AbortSignal) => api.getSales(period, date, signal), [api, period, date]));
  const data = resource.data;
  const hasCompletedHours = (data?.expectedHours ?? 0) > 0;
  const completedBuckets = data?.buckets.filter((bucket) => bucket.expectedHours > 0) ?? [];
  const timeZone = data?.timeZoneId ?? "Europe/Warsaw";

  return <Screen refreshing={resource.loading} onRefresh={() => void resource.refresh()}>
    <Header title="Sales details" subtitle="Export, hourly settlement and data coverage" />
    <Card>
      <TileHeader title={dateCaption(data?.request.date ?? date, period)} subtitle={`${period} report · ${timeZone}`}
        accessibilityScope="Sales details" loading={resource.loading} onRefresh={() => void resource.refresh()} />
      <StatusPill label="Deye estimate" tone="info" />
      <ErrorBanner message={resource.error} />
      {resource.loading && !data ? <LoadingState label="Loading sales details..." /> : null}
      {data ? <>
        <Metric label="Measured export" value={amount(hasCompletedHours ? data.exportKwh : null, "kWh")} />
        <Metric label="Export credited after hourly netting" value={amount(hasCompletedHours ? data.creditedExportKwh : null, "kWh")} />
        <Metric label="Energy value" value={amount(hasCompletedHours ? data.energyValuePln : null, "PLN")} />
        <Metric label="Estimated deposit credit" value={amount(hasCompletedHours ? data.estimatedDepositPln : null, "PLN")} />
        <Text style={styles.note}>Totals cover completed hours only. Missing readings or prices remain unavailable; they are not counted as zero.</Text>
        {!hasCompletedHours ? <Text style={styles.note}>There are no completed contract hours in this reporting window.</Text> : null}
        {data.updatedAt ? <Text style={styles.note}>Updated {momentCaption(data.updatedAt, timeZone)} · refreshes every 5 minutes</Text> : null}
      </> : null}
    </Card>

    {data ? <>
      <Card>
        <Text style={styles.title}>Completed-hour coverage</Text>
        <Metric label="Expected elapsed contract hours" value={String(data.expectedHours)} />
        <Metric label="Hours with Deye readings" value={`${data.observedHours} of ${data.expectedHours}`} />
        <Metric label="Hours with an energy valuation" value={`${data.valuedHours} of ${data.observedHours} observed`} />
        {data.isPartial ? <Text style={styles.warning}>Partial data · totals cover only available hours.</Text> : null}
        {data.dataError ? <Text style={styles.warning}>{data.dataError}</Text> : null}
        {data.priceError ? <Text style={styles.warning}>{data.priceError}</Text> : null}
        <Text style={styles.note}>Reporting window: {momentCaption(data.start, timeZone)} – {momentCaption(data.end, timeZone)}. The ending time is exclusive.</Text>
      </Card>

      {data.currentHour ? <Card>
        <Text style={styles.title}>Current hour · in progress</Text>
        <StatusPill label="Provisional · excluded from totals" tone="info" />
        <Metric label="Measured export" value={amount(data.currentHour.exportKwh, "kWh")} />
        <Metric label="Export after hourly netting so far" value={amount(data.currentHour.creditedExportKwh, "kWh")} />
        <Metric label="Energy value so far" value={amount(data.currentHour.energyValuePln, "PLN")} />
        <Metric label="Estimated deposit credit so far" value={amount(data.currentHour.estimatedDepositPln, "PLN")} />
        <Metric label="Observed duration" value={amount(data.currentHour.observedSeconds / 60, "min", 1)} />
        <Text style={styles.note}>Hour beginning {momentCaption(data.currentHour.start, timeZone)}.</Text>
        <Text style={styles.note}>{data.currentHour.observedThrough ? `Measured through ${momentCaption(data.currentHour.observedThrough, timeZone)}.` : "Awaiting current-hour readings."}</Text>
        {data.currentHour.exportKwh !== null && data.currentHour.energyValuePln === null ? <Text style={styles.warning}>Awaiting prices for the current hour.</Text> : null}
        <Text style={styles.note}>Measured intervals only; no projection to the end of the hour. Hourly netting and valuation may change before this hour completes.</Text>
      </Card> : null}

      <Card>
        <Text style={styles.title}>Settlement terms</Text>
        <Metric label="TAURON contract starts" value={dateCaption(data.contractStartDate)} />
        <Text style={styles.note}>Grid imports and exports are netted for each hour before credited export is calculated. Energy value uses the available RCE prices under the contract terms.</Text>
        <Text style={styles.note}>Estimated monthly deposit credit includes the 1.23 multiplier. It is not a bank payout or the remaining deposit balance.</Text>
        <Text style={styles.note}>Final settlement uses the OSD billing meter. Deye readings and the values shown here are estimates. Individual RCE rates are not provided in this report.</Text>
      </Card>

      <Text style={styles.title}>Completed intervals</Text>
      <Text style={styles.note}>{period === "Day" ? "Hourly" : period === "Month" ? "Daily" : "Monthly"} breakdown. Values below exclude the current hour.</Text>
      {!completedBuckets.length ? <Text style={styles.note}>No completed intervals are available for this period.</Text> : completedBuckets.map((bucket) => <Card key={bucket.start}>
        <Text style={styles.interval}>{momentCaption(bucket.start, timeZone)} – {momentCaption(bucket.end, timeZone)}</Text>
        <Metric label="Export" value={amount(bucket.exportKwh, "kWh")} />
        <Metric label="Credited export" value={amount(bucket.creditedExportKwh, "kWh")} />
        <Metric label="Energy value" value={amount(bucket.energyValuePln, "PLN")} />
        <Metric label="Estimated deposit credit" value={amount(bucket.estimatedDepositPln, "PLN")} />
        <Text style={styles.note}>{bucket.observedHours} of {bucket.expectedHours} completed hours observed · {bucket.valuedHours} valued.</Text>
        {bucket.observedHours < bucket.expectedHours || bucket.valuedHours < bucket.observedHours ? <Text style={styles.warning}>Partial interval · missing hours are not zero.</Text> : null}
      </Card>)}
    </> : null}
  </Screen>;
}

function Metric({ label, value }: { label: string; value: string }) {
  return <View style={styles.metric}><Text style={styles.label}>{label}</Text><Text style={styles.value} selectable>{value}</Text></View>;
}

const styles = StyleSheet.create({
  title: { color: colors.text, fontSize: typography.section, fontWeight: "700" },
  interval: { color: colors.text, fontSize: typography.body, fontWeight: "600" },
  metric: { flexDirection: "row", flexWrap: "wrap", alignItems: "center", justifyContent: "space-between", gap: spacing.sm },
  label: { color: colors.muted, fontSize: typography.caption, lineHeight: 18, flexShrink: 1 },
  value: { color: colors.text, fontSize: 18, fontWeight: "700", fontVariant: ["tabular-nums"] },
  note: { color: colors.muted, fontSize: typography.caption, lineHeight: 18 },
  warning: { color: colors.amber, fontSize: typography.caption, lineHeight: 18 }
});
