import { useDemoDisplayName } from "../demo/useDemoDisplayName";
import { formattingLocale } from "../../core/i18n";
import { useLanguage } from "../../application/LanguageContext";
import { useCallback, useEffect, useRef, useState } from "react";
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
import { formatDateTime, formatWatts } from "../../core/format";
import { colors, spacing, typography } from "../../core/theme";
import { useAuth } from "../../application/AuthContext";

type HistoryMode = "readings" | "runs";
type HoursValue = "1" | "6" | "24" | "168";
type RunFilter = "ALL" | "ON" | "OFF" | "CHANGES";

export function HistoryScreen() {
  const { t } = useLanguage();
  const { api } = useAuth();
  const [mode, setMode] = useState<HistoryMode>("runs");
  const [hours, setHours] = useState<HoursValue>("6");
  const [filter, setFilter] = useState<RunFilter>("ALL");
  const [readings, setReadings] = useState<Reading[]>([]);
  const [runs, setRuns] = useState<RuleRunLog[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const requestSeq = useRef(0);

  const load = useCallback(async (refresh = false) => {
    const requestId = ++requestSeq.current;
    setError(null);
    refresh ? setRefreshing(true) : setLoading(true);

    try {
      if (mode === "readings") {
        const result = await api.getReadings(Number(hours));
        if (requestId === requestSeq.current) {
          setReadings(result);
        }
      } else {
        const result = await api.getRuleRuns(Number(hours), filter);
        if (requestId === requestSeq.current) {
          setRuns(result);
        }
      }
    } catch (ex) {
      if (requestId === requestSeq.current) {
        setError(ex instanceof Error ? ex.message : "Unable to load history.");
      }
    } finally {
      if (requestId === requestSeq.current) {
        setLoading(false);
        setRefreshing(false);
      }
    }
  }, [api, filter, hours, mode]);

  useEffect(() => {
    void load(false);
  }, [load]);

  const items: Array<Reading | RuleRunLog> = loading
    ? []
    : mode === "readings"
      ? readings
      : runs;

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
            onRefresh={() => void load(true)}
            tintColor={colors.primary}
          />
        )}
        ListHeaderComponent={(
          <View style={styles.controls}>
            <Header
              title={t("History")}
              subtitle={mode === "readings" ? t("{0} readings", readings.length) : t("{0} rule runs", runs.length)}
              action={<AppButton label={t("Refresh")} icon={RefreshCcw} onPress={() => void load(true)} loading={refreshing} disabled={refreshing} variant="secondary" compact />}
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
      <View style={styles.topRow}>
        <Text style={styles.time}>{formatDateTime(reading.timestamp)}</Text>
        <StatusPill label={reading.dataSource} tone="info" />
      </View>
      <View style={styles.grid}>
        <DataPoint label="SOC" value={`${reading.batterySoc}%`} />
        <DataPoint label={t("Solar")} value={formatWatts(reading.solarProduction)} />
        <DataPoint label={t("Battery")} value={formatWatts(reading.batteryPower)} />
        <DataPoint label={t("Grid")} value={formatWatts(reading.gridConsumption)} />
        <DataPoint label={t("Load")} value={formatWatts(reading.loadPower)} />
        <DataPoint label={t("Voltage")} value={`${reading.batteryVoltage.toLocaleString(formattingLocale(), { minimumFractionDigits: 1, maximumFractionDigits: 1 })} V`} />
        <DataPoint label={t("Temp")} value={`${reading.batteryTemperature.toLocaleString(formattingLocale(), { minimumFractionDigits: 1, maximumFractionDigits: 1 })} C`} />
      </View>
    </Card>
  );
}

function RunCard({ run }: { run: RuleRunLog }) {
  const demoDisplayName = useDemoDisplayName();
  const { t } = useLanguage();
  return (
    <Card style={styles.card}>
      <View style={styles.topRow}>
        <View style={styles.titleGroup}>
          <Text style={styles.name} numberOfLines={1}>{demoDisplayName(run.ruleName)}</Text>
          <Text style={styles.time}>{formatDateTime(run.timestamp)}</Text>
        </View>
        <StatusPill
          label={run.action === "NO_CHANGE" ? t("No change") : t(run.action)}
          tone={run.action === "ON" ? "success" : run.action === "OFF" ? "danger" : "neutral"}
        />
      </View>
      <View style={styles.grid}>
        <DataPoint label="SOC" value={`${run.batterySoc}%`} />
        <DataPoint label={t("Solar")} value={formatWatts(run.solarProduction)} />
        <DataPoint label={t("Battery")} value={formatWatts(run.batteryPower)} />
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
  topRow: {
    flexDirection: "row",
    alignItems: "flex-start",
    justifyContent: "space-between",
    gap: spacing.md
  },
  titleGroup: {
    flex: 1,
    gap: spacing.xs
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
