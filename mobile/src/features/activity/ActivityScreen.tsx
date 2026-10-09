import { useCallback, useMemo, useState } from "react";
import { View } from "react-native";
import { useScreenRefresh } from "../../core/ScreenRefreshContext";
import { useAuth } from "../../application/AuthContext";
import { useLanguage } from "../../application/LanguageContext";
import { useScopedAction } from "../../application/useScopedAction";
import type { ActivityChecks, ActivityFeed, ActivityItem } from "../../core/api/redesignTypes";
import { AppButton, Banner, Card, DataRow, EmptyState, ErrorBanner, Header, LoadingState, Screen, SegmentedControl, ThemedText as Text } from "../../core/components";
import { formatDateTime, formatNumber } from "../../core/format";
import { useTheme } from "../../ui/theme/ThemeProvider";
import { SelectField } from "../../ui/forms/SelectField";
import { useFocusedResource } from "../energy/useFocusedResource";
import { useDemoDisplayName } from "../demo/useDemoDisplayName";
import { ruleDecisionLabel } from "../rules/rulePresentation";
import { ActivityRow } from "./ActivityRow";
import { ActivityNavigation } from "./ActivityNavigation";

export function ActivityScreen() {
  const { api } = useAuth(); const { colors } = useTheme(); const { t } = useLanguage(); const name = useDemoDisplayName();
  const [hours, setHours] = useState<"24" | "168" | "720">("24"); const [ruleId, setRuleId] = useState("all"); const [filter, setFilter] = useState<"all" | "changes">("all");
  const [expanded, setExpanded] = useState<number | null>(null); const [pages, setPages] = useState<{ baseline: ActivityFeed | null; values: ActivityFeed[] }>({ baseline: null, values: [] }); const [error, setError] = useState<string | null>(null);
  const window = useMemo(() => { const end = new Date(); return { from: new Date(end.getTime() - Number(hours) * 3600000).toISOString(), to: end.toISOString() }; }, [hours]);
  const options = useMemo(() => ({ ...window, ...(ruleId === "all" ? {} : { ruleId: Number(ruleId) }), changesOnly: filter === "changes" }), [window, ruleId, filter]);
  const rules = useFocusedResource("activity-rules", useCallback(() => api.getRules(), [api]), api);
  const feed = useFocusedResource(`activity:${hours}:${ruleId}:${filter}`, useCallback((signal: AbortSignal) => api.getActivity(options, signal), [api, options]), api);
  const actions = useScopedAction(api, `${hours}:${ruleId}:${filter}`, () => { setPages({ baseline: null, values: [] }); setError(null); setExpanded(null); });
  const values = pages.baseline === feed.data ? [feed.data, ...pages.values].filter((page): page is ActivityFeed => page !== null) : feed.data ? [feed.data] : [];
  const items = [...new Map(values.flatMap(page => page.items).map(item => [item.id, item])).values()]; const cursor = values.at(-1)?.nextCursor;
  const next = () => actions.run("page", async context => { if (!cursor) return; const result = await api.getActivity({ ...options, cursor }, context.signal); context.publish(() => setPages(current => ({ baseline: feed.data, values: current.baseline === feed.data ? [...current.values, result] : [result] }))); }, { started: () => setError(null), failed: exception => setError(exception instanceof Error ? exception.message : "More activity could not be loaded.") });
  return <Screen refreshing={feed.loading || rules.loading || actions.busy !== null} onRefresh={async () => { await Promise.all([feed.refresh(true), rules.refresh(true)]); }}>
    <Header title="Activity" subtitle="Recorded checks, requests and observations" />
    <ActivityNavigation value="automations" />
    <SegmentedControl value={hours} onChange={setHours} options={[{ label: "24h", value: "24" }, { label: "7d", value: "168" }, { label: "30d", value: "720" }]} />
    <SelectField label="Automation" value={ruleId} onChange={setRuleId} options={[{ value: "all", label: t("All automations") }, ...(rules.data ?? []).map(rule => ({ value: String(rule.id), label: name(rule.name) }))]} />
    <SegmentedControl value={filter} onChange={setFilter} options={[{ label: "All activity", value: "all" }, { label: "Changes only", value: "changes" }]} />
    <ErrorBanner message={feed.error ?? rules.error ?? error} />
    {feed.data ? <Card style={{ gap: 8 }}><View style={{ flexDirection: "row", gap: 16 }}><View style={{ flex: 1 }}><Text style={{ fontSize: 26, fontWeight: "700" }}>{formatNumber(feed.data.summary.switches, 0)}</Text><Text style={{ fontSize: 13, color: colors.ink2 }}>{t("Switch activity")}</Text></View><View style={{ flex: 1 }}><Text style={{ fontSize: 26, fontWeight: "700" }}>{feed.data.summary.onSeconds === null ? "—" : t("{0} h", formatNumber(feed.data.summary.onSeconds / 3600, 1))}</Text><Text style={{ fontSize: 13, color: colors.ink2 }}>{t("Observed on time")}</Text></View></View><Text style={{ fontSize: 13, color: colors.ink3 }}>{t("{0} provider confirmations", feed.data.summary.confirmedCommands)}</Text>{feed.data.summary.partial ? <Text style={{ color: colors.warningText, fontSize: 13 }}>{t("Partial observation coverage. Unknown intervals are not counted as off.")}</Text> : null}</Card> : null}
    {feed.loading && !feed.data ? <LoadingState label="Loading activity..." /> : !items.length ? <EmptyState title="No activity in this range" detail="Recorded events will appear here when available." /> : items.map(item => <Card key={item.id} style={{ paddingVertical: 0, paddingHorizontal: 16 }}><ActivityRow item={item} onPress={item.checkCount > 0 ? () => setExpanded(current => current === item.id ? null : item.id) : undefined} />{expanded === item.id ? <CheckGroup item={item} /> : null}</Card>)}
    {cursor ? <AppButton label="Show earlier activity" variant="quiet" loading={actions.busy === "page"} disabled={actions.busy !== null} onPress={() => void next()} /> : null}
    <Banner><Text style={{ color: colors.ink2, fontSize: 13 }}>{t("A request asks the provider to switch. A confirmation records its response. Only device observations establish on time.")}</Text></Banner>
  </Screen>;
}

function CheckGroup({ item }: { item: ActivityItem }) {
  const { api } = useAuth(); const { colors } = useTheme(); const { t } = useLanguage();
  const resource = useFocusedResource(`activity-group:${item.id}`, useCallback((signal: AbortSignal) => api.getActivityChecks(item.id, undefined, signal), [api, item.id]), api);
  const [pages, setPages] = useState<{ baseline: ActivityChecks | null; values: ActivityChecks[] }>({ baseline: null, values: [] }); const [error, setError] = useState<string | null>(null);
  const actions = useScopedAction(api, `checks:${item.id}`, () => { setPages({ baseline: null, values: [] }); setError(null); });
  useScreenRefresh(() => {}, actions.busy !== null);
  const values = pages.baseline === resource.data ? [resource.data, ...pages.values].filter((page): page is ActivityChecks => page !== null) : resource.data ? [resource.data] : [];
  const checks = [...new Map(values.flatMap(page => page.items).map(check => [check.id, check])).values()]; const cursor = values.at(-1)?.nextCursor;
  const next = () => actions.run("page", async context => { if (!cursor) return; const value = await api.getActivityChecks(item.id, cursor, context.signal); context.publish(() => setPages(current => ({ baseline: resource.data, values: current.baseline === resource.data ? [...current.values, value] : [value] }))); }, { failed: exception => setError(exception instanceof Error ? exception.message : "More checks could not be loaded.") });
  return <View style={{ gap: 8, borderTopColor: colors.line, borderTopWidth: 1, paddingVertical: 12 }}><ErrorBanner message={resource.error ?? error} />{resource.loading && !resource.data ? <LoadingState label="Loading checks..." /> : checks.map(check => <View key={check.id} style={{ gap: 2, borderBottomColor: colors.line, borderBottomWidth: 1, paddingBottom: 8 }}><Text style={{ fontSize: 13, fontWeight: "600" }}>{formatDateTime(check.occurredAt)}</Text><Text style={{ fontSize: 13, color: colors.ink2 }}>{t(ruleDecisionLabel(check.reasonCode ?? "not_checked"))}</Text><DataRow label="Battery charge" value={check.batterySoc === null ? "—" : `${formatNumber(check.batterySoc, 0)}%`} /><DataRow label="Solar power" value={check.solarWatts === null ? "—" : `${formatNumber(check.solarWatts / 1000, 2)} kW`} /></View>)}{cursor ? <AppButton label="Show earlier checks" variant="quiet" compact disabled={actions.busy !== null} loading={actions.busy === "page"} onPress={() => void next()} /> : null}</View>;
}
