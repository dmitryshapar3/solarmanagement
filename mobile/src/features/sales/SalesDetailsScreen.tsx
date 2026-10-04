import { useLanguage } from "../../application/LanguageContext";
import { ScrollView, StyleSheet, Text, View } from "react-native";
import { RouteProp, useRoute } from "@react-navigation/native";
import { RootStackParamList } from "../../application/navigationTypes";
import { Card, Header, Screen, StatusPill } from "../../core/components";
import { ExportSalesResult } from "../../core/api/types";
import { formatNumber } from "../../core/format";
import { colors, spacing, typography } from "../../core/theme";
import { amount, dateCaption, momentCaption } from "../energy/chartPolicy";
import { SalesPanel, SalesPeriod } from "./SalesScreen";

export function SalesDetailsScreen() {
  const { t } = useLanguage();
  const route = useRoute<RouteProp<RootStackParamList, "SalesDetails">>();
  return <Screen>
    <Header title={t("Sales details")} subtitle={t("Interactive export history, hourly settlement and data coverage")} />
    <SalesPanel initialPeriod={route.params?.period} initialDate={route.params?.date} showDetails={false} chartFirst
      renderDetails={(data, period) => <SalesReportDetails data={data} period={period} />} />
  </Screen>;
}

function SalesReportDetails({ data, period }: { data: ExportSalesResult; period: SalesPeriod }) {
  const { t } = useLanguage();
  const completedBuckets = data.buckets.filter((bucket) => bucket.expectedHours > 0);
  const timeZone = data.timeZoneId;
  return <>
      <Card>
        <Text style={styles.title}>{t("Completed-hour coverage")}</Text>
        <Metric label={t("Export credited after hourly netting")} value={amount(data.expectedHours > 0 ? data.creditedExportKwh : null, "kWh")} />
        <Metric label={t("Expected elapsed contract hours")} value={formatNumber(data.expectedHours)} />
        <Metric label={t("Hours with inverter readings")} value={t("{0} of {1}", data.observedHours, data.expectedHours)} />
        <Metric label={t("Hours with an energy valuation")} value={t("{0} of {1} observed", data.valuedHours, data.observedHours)} />
        {data.isPartial ? <Text style={styles.warning}>{t("Partial data · totals cover only available hours.")}</Text> : null}
        {data.dataError ? <Text style={styles.warning}>{t(data.dataError)}</Text> : null}
        {data.priceError ? <Text style={styles.warning}>{t(data.priceError)}</Text> : null}
        <Text style={styles.note}>{t("Reporting window: {0} – {1}. The ending time is exclusive.", momentCaption(data.start, timeZone), momentCaption(data.end, timeZone))}</Text>
      </Card>

      {data.currentHour ? <Card>
        <Text style={styles.title}>{t("Current hour · in progress")}</Text>
        <StatusPill label={t("Provisional · excluded from totals")} tone="info" />
        <Metric label={t("Measured export")} value={amount(data.currentHour.exportKwh, "kWh")} />
        <Metric label={t("Export after hourly netting so far")} value={amount(data.currentHour.creditedExportKwh, "kWh")} />
        <Metric label={t("Energy value so far")} value={amount(data.currentHour.energyValuePln, "PLN")} />
        <Metric label={t("Estimated deposit credit so far")} value={amount(data.currentHour.estimatedDepositPln, "PLN")} />
        <Metric label={t("Observed duration")} value={amount(data.currentHour.observedSeconds / 60, "min", 1)} />
        <Text style={styles.note}>{t("Hour beginning {0}.", momentCaption(data.currentHour.start, timeZone))}</Text>
        <Text style={styles.note}>{data.currentHour.observedThrough ? t("Measured through {0}.", momentCaption(data.currentHour.observedThrough, timeZone)) : t("Awaiting current-hour readings.")}</Text>
        {data.currentHour.exportKwh !== null && data.currentHour.energyValuePln === null ? <Text style={styles.warning}>{t("Awaiting prices for the current hour.")}</Text> : null}
        <Text style={styles.note}>{t("Measured intervals only; no projection to the end of the hour. Hourly netting and valuation may change before this hour completes.")}</Text>
      </Card> : null}

      <Card>
        <Text style={styles.title}>{t("Settlement terms")}</Text>
        <Metric label={t("TAURON contract starts")} value={dateCaption(data.contractStartDate)} />
        <Text style={styles.note}>{t("Grid imports and exports are netted for each hour before credited export is calculated. Energy value uses the available RCE prices under the contract terms.")}</Text>
        <Text style={styles.note}>{t("Estimated monthly deposit credit includes the 1.23 multiplier. It is not a bank payout or the remaining deposit balance.")}</Text>
        <Text style={styles.note}>{t("Final settlement uses the OSD billing meter. inverter readings and the values shown here are estimates. Individual RCE rates are not provided in this report.")}</Text>
      </Card>

      <Text style={styles.title}>{t("Completed intervals")}</Text>
      <Text style={styles.note}>{t("{0} breakdown. Values below exclude the current hour.", period === "Day" ? t("Hourly") : period === "Month" ? t("Daily") : t("Monthly"))}</Text>
      {!completedBuckets.length ? <Text style={styles.note}>{t("No completed intervals are available for this period.")}</Text> : <Card>
        <Text style={styles.note}>{t("Swipe horizontally for credited export, deposit and coverage. A dash means unavailable; measured zero remains 0.00.")}</Text>
        <ScrollView horizontal nestedScrollEnabled>
          <View>
            <View style={styles.tableRow}>{[t("Interval starting"), t("Export kWh"), t("Credited kWh"), t("Value PLN"), t("Deposit PLN"), t("Observed hours"), t("Valued hours")].map((label, index) => <Text key={label} style={[styles.cell, styles.tableHeader, index === 0 && styles.timeCell]}>{t(label)}</Text>)}</View>
            {completedBuckets.map((bucket) => <View key={bucket.start} style={styles.tableRow}>
              <Text style={[styles.cell, styles.timeCell]}>{momentCaption(bucket.start, timeZone)}</Text>
              <Text selectable style={styles.cell}>{amount(bucket.exportKwh, "", 2).trim()}</Text>
              <Text selectable style={styles.cell}>{amount(bucket.creditedExportKwh, "", 2).trim()}</Text>
              <Text selectable style={styles.cell}>{amount(bucket.energyValuePln, "", 2).trim()}</Text>
              <Text selectable style={styles.cell}>{amount(bucket.estimatedDepositPln, "", 2).trim()}</Text>
              <Text style={styles.cell}>{formatNumber(bucket.observedHours)} / {formatNumber(bucket.expectedHours)}</Text>
              <Text style={styles.cell}>{formatNumber(bucket.valuedHours)} / {formatNumber(bucket.observedHours)}</Text>
            </View>)}
          </View>
        </ScrollView>
        <Text style={styles.note}>{t("Observed hours are compared with expected completed contract hours. Valued hours are compared with observed hours. Missing hours are not zero; partial coverage applies wherever either count is incomplete.")}</Text>
        <Text style={styles.note}>{t("Times include the UTC offset. For Month each row groups a day; for Year each row groups a month. Values exclude the current hour, which is shown separately above.")}</Text>
      </Card>}

  </>;
}

function Metric({ label, value }: { label: string; value: string }) {
  const { t } = useLanguage();
  return <View style={styles.metric}><Text style={styles.label}>{t(label)}</Text><Text style={styles.value} selectable>{value}</Text></View>;
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
