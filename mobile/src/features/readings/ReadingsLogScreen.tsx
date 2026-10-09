import { useCallback, useState } from "react";
import { FlatList, RefreshControl, View } from "react-native";
import { useAuth } from "../../application/AuthContext";
import { useLanguage } from "../../application/LanguageContext";
import { useScopedAction } from "../../application/useScopedAction";
import type { ReadingsView } from "../../core/api/redesignTypes";
import { AppButton, Banner, Card, EmptyState, ErrorBanner, Header, LoadingState, Screen, SegmentedControl, ThemedText as Text } from "../../core/components";
import { formatDateTime } from "../../core/format";
import { useTheme } from "../../ui/theme/ThemeProvider";
import { useFocusedResource } from "../energy/useFocusedResource";
import { ReadingMetrics } from "./ReadingMetrics";
import { ActivityNavigation } from "../activity/ActivityNavigation";
export function ReadingsLogScreen() {
  const { api } = useAuth(); const { colors } = useTheme(); const { t } = useLanguage();
  const [hours, setHours] = useState<"1" | "6" | "24" | "168">("6"); const [aggregate, setAggregate] = useState<"raw" | "5m">("5m"); const [pages, setPages] = useState<{ baseline: ReadingsView | null; values: ReadingsView[] }>({ baseline: null, values: [] }); const [error, setError] = useState<string | null>(null);
  const resource = useFocusedResource(`readings:${hours}:${aggregate}`, useCallback((signal: AbortSignal) => api.getReadingsView(Number(hours), aggregate, undefined, signal), [api, hours, aggregate]), api);
  const actions = useScopedAction(api, `${hours}:${aggregate}`, () => { setPages({ baseline: null, values: [] }); setError(null); });
  const values = pages.baseline === resource.data ? [resource.data, ...pages.values].filter((page): page is ReadingsView => page !== null) : resource.data ? [resource.data] : [];
  const items = values.flatMap(page => page.items); const gaps = values.flatMap(page => page.gaps); const cursor = values.at(-1)?.nextCursor;
  const next = () => actions.run("page", async context => { if (!cursor) return; const result = await api.getReadingsView(Number(hours), aggregate, cursor, context.signal); context.publish(() => setPages(current => ({ baseline: resource.data, values: current.baseline === resource.data ? [...current.values, result] : [result] }))); }, { started: () => setError(null), failed: exception => setError(exception instanceof Error ? exception.message : "More readings could not be loaded.") });
  const busy = resource.loading || actions.busy !== null;
  return <Screen scroll={false} style={{ flex: 1, padding: 0, gap: 0 }}><FlatList data={items} keyExtractor={row => `${row.id}:${row.timestamp}:${row.inverterId}`} initialNumToRender={5} maxToRenderPerBatch={5} windowSize={5} contentContainerStyle={{ padding: 16, paddingBottom: 120, gap: 16 }} contentInsetAdjustmentBehavior="never" refreshControl={<RefreshControl refreshing={resource.loading} onRefresh={() => { if (!busy) void resource.refresh(true); }} tintColor={colors.ink} />} ListHeaderComponent={<View style={{ gap: 16 }}><Header title="Activity" subtitle="Reported measurements and data gaps" /><ActivityNavigation value="readings" /><SegmentedControl value={hours} onChange={setHours} options={[{ label: "1h", value: "1" }, { label: "6h", value: "6" }, { label: "24h", value: "24" }, { label: "7d", value: "168" }]} /><SegmentedControl value={aggregate} onChange={setAggregate} options={[{ label: "Raw readings", value: "raw" }, { label: "5-minute averages", value: "5m" }]} /><ErrorBanner message={resource.error ?? error} />
    {resource.loading && !resource.data ? <LoadingState label="Loading history..." /> : !items.length ? <EmptyState title="No readings in range." /> : null}
    {gaps.length ? <Banner tone="warning"><Text style={{ fontSize: 13, color: colors.warningText }}>{t("There are {0} gaps in this range. Missing readings are not zero.", gaps.length)}</Text>{gaps.slice(0, 8).map(gap => <Text key={`${gap.from}:${gap.to}`} style={{ fontSize: 13, color: colors.warningText }}>{formatDateTime(gap.from)}–{formatDateTime(gap.to)}</Text>)}</Banner> : null}
</View>} renderItem={({item:row}) => <Card style={{ gap: 12, padding: 16 }}><View style={{ gap: 2 }}><Text style={{ fontWeight: "600", fontSize: 15 }}>{formatDateTime(row.timestamp)}</Text><Text style={{ fontSize: 13, color: colors.ink3 }}>{t("Source: {0}", row.dataSource)}</Text>{row.solarObservedAt ? <Text style={{ fontSize: 13, color: colors.ink3 }}>{t("Solar measured {0}", formatDateTime(row.solarObservedAt))}</Text> : null}</View><ReadingMetrics row={row} /></Card>}
 ListFooterComponent={<View style={{gap:16}}>    {cursor ? <AppButton label="Show earlier readings" variant="quiet" loading={actions.busy === "page"} disabled={actions.busy !== null} onPress={() => void next()} /> : null}
    <Text style={{ fontSize: 13, color: colors.ink2 }}>{t("Grid import and battery discharge are positive; export and charging are negative. A dash means unavailable; measured zero stays zero.")}</Text>
  </View>}/></Screen>;
}
