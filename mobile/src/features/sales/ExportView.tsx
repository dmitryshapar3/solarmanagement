import { useCallback, useEffect, useMemo, useState } from "react";
import { View } from "react-native";
import { useNavigation } from "@react-navigation/native";
import type { NativeStackNavigationProp } from "@react-navigation/native-stack";
import { Info, Table2 } from "lucide-react-native";
import { useScreenRefresh } from "../../core/ScreenRefreshContext";
import { useAuth } from "../../application/AuthContext";
import { useLanguage } from "../../application/LanguageContext";
import { useScopedAction } from "../../application/useScopedAction";
import type { RootStackParamList } from "../../application/navigationTypes";
import type { ExportSalesResult } from "../../core/api/types";
import { AppButton, Banner, Card, DataRow, ErrorBanner, LoadingState, SectionTitle, SegmentedControl, ThemedText as Text } from "../../core/components";
import { useTheme } from "../../ui/theme/ThemeProvider";
import { ExportChart } from "../../ui/charts/ExportChart";
import { amount, dateCaption, momentCaption, salesPointValues, tickCaption, zonedDate, type ChartPoint } from "../energy/chartPolicy";
import { energyRequestRange, energySelectionKey, energyWindow, salesPeriod, type EnergySelection } from "../energy/energySelection";
import { useFocusedResource } from "../energy/useFocusedResource";
import { measuredSalesRecheckRange } from "./exportPolicy";

export function exportPoints(data: ExportSalesResult, metric: "energy" | "value", t: (phrase: string, ...values: unknown[]) => string): ChartPoint[] {
  const unit = metric === "energy" ? "kWh" : "PLN";
  const period = data.request.period === 0 ? "Day" : data.request.period === 2 ? "Year" : "Month";
  return data.buckets.map(bucket => {
    const { current, completed, provisional } = salesPointValues(bucket, data.currentHour, metric);
    return { timestamp: bucket.start, label: tickCaption(bucket.start, data.timeZoneId, period), completed, provisional,
      description: t("{0}–{1} · Completed {2}{3}{4}", momentCaption(bucket.start, data.timeZoneId), momentCaption(bucket.end, data.timeZoneId), amount(completed, unit), current ? t(" · In progress {0}, not in totals", amount(provisional, unit)) : "", bucket.observedHours < bucket.expectedHours || bucket.valuedHours < bucket.observedHours ? t(" · Partial interval") : "") };
  });
}
export function ExportView({ selection, onTodayResolved }: { selection: EnergySelection; onTodayResolved: (today: string) => void }) {
  const { api } = useAuth(); const { colors } = useTheme(); const { t } = useLanguage(); const navigation = useNavigation<NativeStackNavigationProp<RootStackParamList>>();
  const period = salesPeriod(selection.period); const date = selection.date;
  const [metric, setMetric] = useState<"energy" | "value">("energy"); const [how, setHow] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const key = energySelectionKey(selection);
  const resource = useFocusedResource(`export:${key}`, useCallback(async (signal: AbortSignal) => {
    const site = await api.getSiteSettings(signal); const selected = date ?? zonedDate(new Date(), site.solarSales.timeZoneId);
    return api.getSalesDetails(period, selected, signal, energyRequestRange(selection, selected), { includeUpcoming: true });
  }, [api, period, date, selection.from, selection.through]), api);
  const data = resource.data; const today = data?.today ?? zonedDate(new Date()); const selected = data?.request.date ?? date ?? today;
  useEffect(() => { if (data?.today) onTodayResolved(data.today); }, [data?.today, onTodayResolved]);
  const range = energyRequestRange(selection, today);
  const window = energyWindow(selection, today);
  const recheckRange = measuredSalesRecheckRange(window.from, window.through, today);
  const actions = useScopedAction(api, key, () => { setError(null); });
  useScreenRefresh(() => {}, actions.busy !== null);
  const points = useMemo(() => data ? exportPoints(data, metric, t) : [], [data, metric, t]);
  const recheck = () => actions.run("prices", async context => {
    if (!recheckRange) return;
    await api.recheckSalesPrices("Custom", recheckRange.from, recheckRange, context.signal);
    if (context.isCurrent()) await resource.refresh();
  }, { started: () => setError(null), failed: exception => setError(exception instanceof Error ? exception.message : "Prices could not be checked.") });
  return <>
    {energyWindow(selection, today).through > today ? <Banner><Text style={{ fontSize: 13, color: colors.ink2 }}>{t("Future export and value are unavailable until measured. View the generation forecast on the Production tab.")}</Text></Banner> : null}
    <ErrorBanner message={resource.error ?? error} />
    {resource.loading && !data ? <LoadingState label="Loading sales..." /> : null}
    <View style={{ gap: 4, paddingHorizontal: 4 }}><Text style={{ fontSize: 52, lineHeight: 58, fontWeight: "700", letterSpacing: -1.82 }}>{amount((data?.expectedHours ?? 0) > 0 ? data?.exportKwh : null, "kWh")}</Text><Text style={{ color: colors.ink2, fontSize: 15 }}>{t("Exported to the grid in completed hours")}</Text><View style={{ flexDirection: "row", flexWrap: "wrap", gap: 12, marginTop: 8 }}><Text style={{ fontWeight: "600" }}>{amount(data?.energyValuePln, "PLN")} <Text style={{ color: colors.ink2, fontSize: 13 }}>{t("Value, estimated")}</Text></Text><Text style={{ fontWeight: "600" }}>{amount(data?.estimatedDepositPln, "PLN")} <Text style={{ color: colors.ink2, fontSize: 13 }}>{t("Deposit credit, estimated")}</Text></Text></View></View>
    {data && (data.missingPriceHours?.length || data.valuedHours < data.observedHours || data.priceError) ? <Banner tone="warning"><View style={{ gap: 10 }}><Text style={{ fontSize: 15, fontWeight: "600", color: colors.warningText }}>{t("Some export hours have no price")}</Text><Text style={{ fontSize: 13, color: colors.warningText }}>{data.missingPriceHours?.length ? data.missingPriceHours.slice(0, 8).map(hour => momentCaption(hour, data.timeZoneId)).join(" · ") + (data.missingPriceHours.length > 8 ? t(" · {0} more", data.missingPriceHours.length - 8) : "") : t("Prices are unavailable for {0} measured hours.", data.observedHours - data.valuedHours)}</Text><Text style={{ fontSize: 13, color: colors.warningText }}>{t("Missing prices are never counted as zero. Totals cover priced intervals only.")}</Text><AppButton label="Check prices again" loading={actions.busy === "prices"} disabled={actions.busy !== null || !recheckRange} compact variant="secondary" onPress={() => void recheck()} /></View></Banner> : null}
    <Card style={{ padding: 16, gap: 12 }}><SegmentedControl options={[{ label: "kWh", value: "energy" }, { label: "PLN", value: "value" }]} value={metric} onChange={setMetric} /><View style={{ flexDirection: "row", gap: 16 }}><Text style={{ fontSize: 13, color: colors.grid }}>{t("■ Completed hour")}</Text><Text style={{ fontSize: 13, color: colors.ink2 }}>{t("□ In progress")}</Text></View><ExportChart key={`${key}:${metric}`} points={points} unit={metric === "energy" ? "kWh" : "PLN"} /><View style={{ flexDirection: "row", gap: 8 }}><View style={{ flex: 1 }}><AppButton label="Hourly table" icon={Table2} compact variant="quiet" onPress={() => navigation.navigate("ExportHourlySheet", { period, date: selected, ...range, includeUpcoming: true })} /></View><View style={{ flex: 1 }}><AppButton label="How value is estimated" icon={Info} compact variant="quiet" onPress={() => setHow(value => !value)} /></View></View></Card>
    {data?.currentHour ? <Card style={{ gap: 8, borderWidth: 1, borderStyle: "dashed", borderColor: colors.grid }}><SectionTitle title="Current hour · in progress" /><Text style={{ fontSize: 24, lineHeight: 28, fontWeight: "700" }}>{amount(data.currentHour.exportKwh, "kWh")} · {amount(data.currentHour.energyValuePln, "PLN")}</Text><Text style={{ fontSize: 13, color: colors.ink2 }}>{t("Not in totals · measured intervals only, without projection to the end of the hour.")}</Text><Text style={{ fontSize: 13, color: colors.ink2 }}>{data.currentHour.observedThrough ? t("Measured through {0}", momentCaption(data.currentHour.observedThrough, data.timeZoneId)) : t("Awaiting current-hour readings.")}</Text>{data.currentHour.exportKwh !== null && data.currentHour.energyValuePln === null ? <Text style={{ fontSize: 13, color: colors.warningText }}>{t("Awaiting current-hour prices.")}</Text> : null}</Card> : null}
    {data ? <Card style={{ gap: 6 }}><SectionTitle title="Data coverage" /><DataRow label="Measured hours" value={`${data.observedHours} / ${data.expectedHours}`} /><DataRow label="Priced hours" value={`${data.valuedHours} / ${data.observedHours}`} /><DataRow label="Credited after hourly netting" value={amount(data.creditedExportKwh, "kWh")} />{data.isPartial ? <Text style={{ fontSize: 13, color: colors.warningText }}>{t("Partial data · missing measurements and prices are not zero.")}</Text> : null}<ErrorBanner message={data.dataError ?? data.priceError} /><Text style={{ fontSize: 13, color: colors.ink3 }}>{t("Settlement time zone: {0}", data.timeZoneId)}</Text>{data.updatedAt ? <Text style={{ fontSize: 13, color: colors.ink3 }}>{t("Updated {0}", momentCaption(data.updatedAt, data.timeZoneId))}</Text> : null}</Card> : null}
    {how && data ? <ExportExplanation data={data} /> : null}
  </>;
}
export function ExportExplanation({ data }: { data: ExportSalesResult }) { const { colors } = useTheme(); const { t } = useLanguage(); return <Card style={{ gap: 12 }}><SectionTitle title="How value is estimated" /><DataRow label="Contract start date" value={dateCaption(data.contractStartDate)} /><Text style={{ fontSize: 15, color: colors.ink2 }}>{t("Grid imports and exports are netted for each hour before credited export is calculated. Energy value uses the available RCE prices under the contract terms.")}</Text><Text style={{ fontSize: 15, color: colors.ink2 }}>{t("Estimated deposit credit is energy value × 1.23. It is not a cash payout or your remaining deposit balance.")}</Text><Text style={{ fontSize: 13, color: colors.ink2 }}>{t("Final settlement uses the OSD billing meter. Inverter readings and values shown here are estimates. Missing readings and prices are not zero.")}</Text></Card>; }
