import { useDemoDisplayName } from "../demo/useDemoDisplayName";
import { formattingLocale } from "../../core/i18n";
import { useLanguage } from "../../application/LanguageContext";
import { useCallback, useState } from "react";
import { FlatList, RefreshControl, StyleSheet, Text, View } from "react-native";
import { RefreshCcw } from "lucide-react-native";
import {
  AppButton,
  Card,
  EmptyState,
  ErrorBanner,
  Header,
  LoadingState,
  Screen,
  SegmentedControl,
  StatusPill
} from "../../core/components";
import { Reading, RuleRunLog } from "../../core/api/types";
import { formatDateTime, formatPercent, formatWatts } from "../../core/format";
import { listCardHeaderStyles } from "../../core/listCardHeaderStyles";
import { colors, spacing, typography } from "../../core/theme";
import { useAuth } from "../../application/AuthContext";
import { useFocusedResource } from "../energy/useFocusedResource";

type HistoryMode = "readings" | "runs";
type HoursValue = "1" | "6" | "24" | "168";
type RunFilter = "ALL" | "ON" | "OFF" | "CHANGES";

export function HistoryScreen() {
  const { t } = useLanguage();
  const { api } = useAuth();
  const [mode, setMode] = useState<HistoryMode>("runs");
  const [hours, setHours] = useState<HoursValue>("6");
  const [filter, setFilter] = useState<RunFilter>("ALL");
  const resource = useFocusedResource<Array<Reading | RuleRunLog>>(`history:${mode}:${hours}:${mode === "runs" ? filter : "all"}`,
    useCallback((signal: AbortSignal) => mode === "readings" ? api.getReadings(Number(hours), signal) : api.getRuleRuns(Number(hours), filter, signal),
      [api, filter, hours, mode]), api);
  const items = resource.data ?? [];
  const { loading, error } = resource;
  const refreshing = loading && resource.data !== null;

  return (
    <Screen scroll={false} style={styles.screen}>
      <FlatList
        style={styles.list}
        data={items}
        initialNumToRender={4}
        maxToRenderPerBatch={4}
        windowSize={5}
        removeClippedSubviews
        keyExtractor={(item) => `${mode}-${item.id}`}
        renderItem={({ item }) =>
          mode === "readings"
            ? <ReadingCard reading={item as Reading} />
            : <RunCard run={item as RuleRunLog} />
        }
        contentContainerStyle={styles.listContent}
        keyboardShouldPersistTaps="handled"
        refreshControl={(
          <RefreshControl
            refreshing={refreshing}
            onRefresh={() => void resource.refresh(true)}
            tintColor={colors.primary}
          />
        )}
        ListHeaderComponent={(
          <View style={styles.controls}>
            <Header
              title={t("History")}
              subtitle={mode === "readings" ? t("{0} readings", items.length) : t("{0} rule runs", items.length)}
              action={<AppButton label={t("Refresh")} icon={RefreshCcw} onPress={() => void resource.refresh(true)} loading={refreshing} disabled={loading} variant="secondary" compact />}
            />
            <SegmentedControl
              value={mode}
              onChange={setMode}
              options={[
                { label: t("Runs"), value: "runs" },
                { label: t("Readings"), value: "readings" }
              ]}
            />
            <SegmentedControl
              value={hours}
              onChange={setHours}
              options={[
                { label: t("1h"), value: "1" },
                { label: t("6h"), value: "6" },
                { label: t("24h"), value: "24" },
                { label: t("7d"), value: "168" }
              ]}
            />
            {mode === "runs" ? (
              <SegmentedControl
                value={filter}
                onChange={setFilter}
                options={[
                  { label: t("All"), value: "ALL" },
                  { label: t("ON"), value: "ON" },
                  { label: t("OFF"), value: "OFF" },
                  { label: t("Changes"), value: "CHANGES" }
                ]}
              />
            ) : null}
            <ErrorBanner message={error} />
          </View>
        )}
        ListEmptyComponent={
          loading
            ? <LoadingState label={t("Loading history...")} />
            : <EmptyState title={mode === "readings" ? t("No readings in range.") : t("No rule runs in range.")} />
        }
      />
    </Screen>
  );
}

function ReadingCard({ reading }: { reading: Reading }) {
  const { t } = useLanguage();
  return (
    <Card style={styles.card}>
      <View style={listCardHeaderStyles.topRow}>
        <Text style={styles.time}>{formatDateTime(reading.timestamp)}</Text>
        <StatusPill label={reading.dataSource} tone="info" />
      </View>
      <View style={styles.grid}>
        <DataPoint label="SOC" value={reading.batterySocValid === true ? formatPercent(reading.batterySoc) : "—"} />
        <DataPoint label={t("Solar")} value={reading.solarPowerValid === true ? formatWatts(reading.solarProduction) : "—"} />
        <DataPoint label={t("Battery")} value={reading.batteryPowerValid === true ? formatWatts(reading.batteryPower) : "—"} />
        <DataPoint label={t("Grid")} value={reading.gridPowerValid === true ? formatWatts(reading.gridConsumption) : "—"} />
        <DataPoint label={t("Load")} value={reading.loadPowerValid === true ? formatWatts(reading.loadPower) : "—"} />
        <DataPoint label={t("Voltage")} value={reading.batteryVoltageValid === true ? `${reading.batteryVoltage.toLocaleString(formattingLocale(), { minimumFractionDigits: 1, maximumFractionDigits: 1 })} V` : "—"} />
        <DataPoint label={t("Temp")} value={reading.batteryTemperatureValid === true ? `${reading.batteryTemperature.toLocaleString(formattingLocale(), { minimumFractionDigits: 1, maximumFractionDigits: 1 })} C` : "—"} />
      </View>
    </Card>
  );
}

function RunCard({ run }: { run: RuleRunLog }) {
  const demoDisplayName = useDemoDisplayName();
  const { t } = useLanguage();
  return (
    <Card style={styles.card}>
      <View style={listCardHeaderStyles.topRow}>
        <View style={listCardHeaderStyles.titleGroup}>
          <Text style={styles.name} numberOfLines={1}>{demoDisplayName(run.ruleName)}</Text>
          <Text style={styles.time}>{formatDateTime(run.timestamp)}</Text>
        </View>
        <StatusPill
          label={run.action === "NO_CHANGE" ? t("No change") : t(run.action)}
          tone={run.action === "ON" ? "success" : run.action === "OFF" ? "danger" : "neutral"}
        />
      </View>
      <View style={styles.grid}>
        <DataPoint label="SOC" value={run.batterySoc === null ? "—" : formatPercent(run.batterySoc)} />
        <DataPoint label={t("Solar")} value={run.solarProduction === null ? "—" : formatWatts(run.solarProduction)} />
        <DataPoint label={t("Battery")} value={run.batteryPower === null ? "—" : formatWatts(run.batteryPower)} />
      </View>
      <Text style={styles.reason}>{t(run.reason)}</Text>
    </Card>
  );
}

function DataPoint({ label, value }: { label: string; value: string }) {
  const { t } = useLanguage();
  return (
    <View style={styles.point}>
      <Text style={styles.pointLabel}>{t(label)}</Text>
      <Text style={styles.pointValue}>{value}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  screen: {
    flex: 1,
    paddingHorizontal: 0,
    paddingTop: 0
  },
  listContent: {
    paddingHorizontal: spacing.lg,
    paddingTop: spacing.lg,
    paddingBottom: 112,
    gap: spacing.md
  },
  list: {
    flex: 1
  },
  controls: {
    gap: spacing.lg,
    marginBottom: spacing.xs
  },
  card: {
    gap: spacing.md
  },
  name: {
    color: colors.text,
    fontSize: typography.body,
    fontWeight: "800"
  },
  time: {
    color: colors.muted,
    fontSize: typography.caption
  },
  grid: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: spacing.sm
  },
  point: {
    width: "31%",
    minHeight: 58,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: colors.border,
    borderRadius: 8,
    backgroundColor: colors.surfaceRaised,
    padding: spacing.sm,
    gap: spacing.xs
  },
  pointLabel: {
    color: colors.muted,
    fontSize: 11,
    fontWeight: "700",
    textTransform: "uppercase"
  },
  pointValue: {
    color: colors.text,
    fontSize: 14,
    fontWeight: "800"
  },
  reason: {
    color: colors.muted,
    fontSize: typography.caption,
    lineHeight: 18
  }
});
