import { ScrollView, StyleSheet, Text, View } from "react-native";
import { RouteProp, useRoute } from "@react-navigation/native";
import { RootStackParamList } from "../../application/navigationTypes";
import { Card, Header, Screen, StatusPill } from "../../core/components";
import { ExportSalesResult } from "../../core/api/types";
import { colors, spacing, typography } from "../../core/theme";
import { amount, dateCaption, momentCaption } from "../energy/chartPolicy";
import { SalesPanel, SalesPeriod } from "./SalesScreen";

export function SalesDetailsScreen() {
  const route = useRoute<RouteProp<RootStackParamList, "SalesDetails">>();
  return <Screen>
    <Header title="Sales details" subtitle="Interactive export history, hourly settlement and data coverage" />
    <SalesPanel initialPeriod={route.params?.period} initialDate={route.params?.date} showDetails={false} chartFirst
      renderDetails={(data, period) => <SalesReportDetails data={data} period={period} />} />
  </Screen>;
}

function SalesReportDetails({ data, period }: { data: ExportSalesResult; period: SalesPeriod }) {
  const completedBuckets = data.buckets.filter((bucket) => bucket.expectedHours > 0);
  const timeZone = data.timeZoneId;
  return <>
      <Card>
        <Text style={styles.title}>Completed-hour coverage</Text>
        <Metric label="Export credited after hourly netting" value={amount(data.expectedHours > 0 ? data.creditedExportKwh : null, "kWh")} />
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
      {!completedBuckets.length ? <Text style={styles.note}>No completed intervals are available for this period.</Text> : <Card>
        <Text style={styles.note}>Swipe horizontally for credited export, deposit and coverage. A dash means unavailable; measured zero remains 0.00.</Text>
        <ScrollView horizontal nestedScrollEnabled>
          <View>
            <View style={styles.tableRow}>{["Interval starting", "Export kWh", "Credited kWh", "Value PLN", "Deposit PLN", "Observed hours", "Valued hours"].map((label, index) => <Text key={label} style={[styles.cell, styles.tableHeader, index === 0 && styles.timeCell]}>{label}</Text>)}</View>
            {completedBuckets.map((bucket) => <View key={bucket.start} style={styles.tableRow}>
              <Text style={[styles.cell, styles.timeCell]}>{momentCaption(bucket.start, timeZone)}</Text>
              <Text selectable style={styles.cell}>{amount(bucket.exportKwh, "", 2).trim()}</Text>
              <Text selectable style={styles.cell}>{amount(bucket.creditedExportKwh, "", 2).trim()}</Text>
              <Text selectable style={styles.cell}>{amount(bucket.energyValuePln, "", 2).trim()}</Text>
              <Text selectable style={styles.cell}>{amount(bucket.estimatedDepositPln, "", 2).trim()}</Text>
              <Text style={styles.cell}>{bucket.observedHours} / {bucket.expectedHours}</Text>
              <Text style={styles.cell}>{bucket.valuedHours} / {bucket.observedHours}</Text>
            </View>)}
          </View>
        </ScrollView>
        <Text style={styles.note}>Observed hours are compared with expected completed contract hours. Valued hours are compared with observed hours. Missing hours are not zero; partial coverage applies wherever either count is incomplete.</Text>
        <Text style={styles.note}>Times include the UTC offset. For Month each row groups a day; for Year each row groups a month. Values exclude the current hour, which is shown separately above.</Text>
      </Card>}

  </>;
}

function Metric({ label, value }: { label: string; value: string }) {
  return <View style={styles.metric}><Text style={styles.label}>{label}</Text><Text style={styles.value} selectable>{value}</Text></View>;
}

const styles = StyleSheet.create({
  title: { color: colors.text, fontSize: typography.section, fontWeight: "700" },
  tableRow: { flexDirection: "row", borderBottomWidth: StyleSheet.hairlineWidth, borderBottomColor: colors.border },
  cell: { width: 112, color: colors.text, paddingVertical: spacing.sm, paddingHorizontal: spacing.sm, fontSize: 12, fontVariant: ["tabular-nums"] },
  timeCell: { width: 190 }, tableHeader: { color: colors.muted, fontWeight: "700" },
  metric: { flexDirection: "row", flexWrap: "wrap", alignItems: "center", justifyContent: "space-between", gap: spacing.sm },
  label: { color: colors.muted, fontSize: typography.caption, lineHeight: 18, flexShrink: 1 },
  value: { color: colors.text, fontSize: 18, fontWeight: "700", fontVariant: ["tabular-nums"] },
  note: { color: colors.muted, fontSize: typography.caption, lineHeight: 18 },
  warning: { color: colors.amber, fontSize: typography.caption, lineHeight: 18 }
});
