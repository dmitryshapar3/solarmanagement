import { useCallback, useEffect, useState } from "react";
import { StyleSheet, Text, View } from "react-native";
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
  const { api } = useAuth();
  const [mode, setMode] = useState<HistoryMode>("runs");
  const [hours, setHours] = useState<HoursValue>("6");
  const [filter, setFilter] = useState<RunFilter>("ALL");
  const [readings, setReadings] = useState<Reading[]>([]);
  const [runs, setRuns] = useState<RuleRunLog[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async (refresh = false) => {
    setError(null);
    refresh ? setRefreshing(true) : setLoading(true);

    try {
      if (mode === "readings") {
        setReadings(await api.getReadings(Number(hours)));
      } else {
        setRuns(await api.getRuleRuns(Number(hours), filter));
      }
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Unable to load history.");
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, [api, filter, hours, mode]);

  useEffect(() => {
    void load(false);
  }, [load]);

  if (loading) {
    return (
      <Screen scroll={false}>
        <LoadingState label="Loading history..." />
      </Screen>
    );
  }

  return (
    <Screen refreshing={refreshing} onRefresh={() => void load(true)}>
      <Header
        title="History"
        subtitle={mode === "readings" ? `${readings.length} readings` : `${runs.length} rule runs`}
        action={<AppButton label="Refresh" icon={RefreshCcw} onPress={() => void load(true)} variant="secondary" compact />}
      />
      <SegmentedControl
        value={mode}
        onChange={setMode}
        options={[
          { label: "Runs", value: "runs" },
          { label: "Readings", value: "readings" }
        ]}
      />
      <SegmentedControl
        value={hours}
        onChange={setHours}
        options={[
          { label: "1h", value: "1" },
          { label: "6h", value: "6" },
          { label: "24h", value: "24" },
          { label: "7d", value: "168" }
        ]}
      />
      {mode === "runs" ? (
        <SegmentedControl
          value={filter}
          onChange={setFilter}
          options={[
            { label: "All", value: "ALL" },
            { label: "ON", value: "ON" },
            { label: "OFF", value: "OFF" },
            { label: "Changes", value: "CHANGES" }
          ]}
        />
      ) : null}

      <ErrorBanner message={error} />

      {mode === "readings" ? (
        readings.length ? (
          <View style={styles.list}>
            {readings.map((reading) => (
              <ReadingCard key={reading.id} reading={reading} />
            ))}
          </View>
        ) : (
          <EmptyState title="No readings in range." />
        )
      ) : runs.length ? (
        <View style={styles.list}>
          {runs.map((run) => (
            <RunCard key={run.id} run={run} />
          ))}
        </View>
      ) : (
        <EmptyState title="No rule runs in range." />
      )}
    </Screen>
  );
}

function ReadingCard({ reading }: { reading: Reading }) {
  return (
    <Card style={styles.card}>
      <View style={styles.topRow}>
        <Text style={styles.time}>{formatDateTime(reading.timestamp)}</Text>
        <StatusPill label={reading.dataSource} tone="info" />
      </View>
      <View style={styles.grid}>
        <DataPoint label="SOC" value={`${reading.batterySoc}%`} />
        <DataPoint label="Solar" value={formatWatts(reading.solarProduction)} />
        <DataPoint label="Battery" value={formatWatts(reading.batteryPower)} />
        <DataPoint label="Grid" value={formatWatts(reading.gridConsumption)} />
        <DataPoint label="Load" value={formatWatts(reading.loadPower)} />
        <DataPoint label="Temp" value={`${reading.batteryTemperature.toFixed(1)} C`} />
      </View>
    </Card>
  );
}

function RunCard({ run }: { run: RuleRunLog }) {
  return (
    <Card style={styles.card}>
      <View style={styles.topRow}>
        <View style={styles.titleGroup}>
          <Text style={styles.name} numberOfLines={1}>{run.ruleName}</Text>
          <Text style={styles.time}>{formatDateTime(run.timestamp)}</Text>
        </View>
        <StatusPill
          label={run.action}
          tone={run.action === "ON" ? "success" : run.action === "OFF" ? "danger" : "neutral"}
        />
      </View>
      <View style={styles.grid}>
        <DataPoint label="SOC" value={`${run.batterySoc}%`} />
        <DataPoint label="Solar" value={formatWatts(run.solarProduction)} />
        <DataPoint label="Battery" value={formatWatts(run.batteryPower)} />
      </View>
      <Text style={styles.reason}>{run.reason}</Text>
    </Card>
  );
}

function DataPoint({ label, value }: { label: string; value: string }) {
  return (
    <View style={styles.point}>
      <Text style={styles.pointLabel}>{label}</Text>
      <Text style={styles.pointValue}>{value}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  list: {
    gap: spacing.md
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
