import { useCallback, useEffect, useState } from "react";
import { Alert, StyleSheet, Text, View } from "react-native";
import { NativeStackScreenProps } from "@react-navigation/native-stack";
import { Edit3, Plus, RefreshCcw, Trash2 } from "lucide-react-native";
import {
  AppButton,
  Card,
  EmptyState,
  ErrorBanner,
  Header,
  IconButton,
  LoadingState,
  Screen,
  StatusPill,
  SwitchRow
} from "../../core/components";
import { Rule } from "../../core/api/types";
import { formatDateTime, formatWatts } from "../../core/format";
import { colors, spacing, typography } from "../../core/theme";
import { useAuth } from "../../application/AuthContext";
import { RulesStackParamList } from "../../application/navigationTypes";

type Props = NativeStackScreenProps<RulesStackParamList, "RulesList">;

export function RulesScreen({ navigation }: Props) {
  const { api } = useAuth();
  const [rules, setRules] = useState<Rule[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async (refresh = false) => {
    setError(null);
    refresh ? setRefreshing(true) : setLoading(true);

    try {
      setRules(await api.getRules());
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Unable to load rules.");
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, [api]);

  useEffect(() => {
    const unsubscribe = navigation.addListener("focus", () => {
      void load(false);
    });
    return unsubscribe;
  }, [load, navigation]);

  async function toggleRule(rule: Rule, enabled: boolean) {
    setError(null);
    setRules((current) => current.map((item) => (item.id === rule.id ? { ...item, enabled } : item)));
    try {
      const updated = await api.setRuleEnabled(rule.id, enabled);
      setRules((current) => current.map((item) => (item.id === rule.id ? updated : item)));
    } catch (ex) {
      setRules((current) => current.map((item) => (item.id === rule.id ? rule : item)));
      setError(ex instanceof Error ? ex.message : "Unable to update rule.");
    }
  }

  function confirmDelete(rule: Rule) {
    Alert.alert("Delete rule", rule.name, [
      { text: "Cancel", style: "cancel" },
      {
        text: "Delete",
        style: "destructive",
        onPress: () => void deleteRule(rule)
      }
    ]);
  }

  async function deleteRule(rule: Rule) {
    setError(null);
    try {
      await api.deleteRule(rule.id);
      setRules((current) => current.filter((item) => item.id !== rule.id));
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Unable to delete rule.");
    }
  }

  if (loading) {
    return (
      <Screen scroll={false}>
        <LoadingState label="Loading rules..." />
      </Screen>
    );
  }

  return (
    <Screen refreshing={refreshing} onRefresh={() => void load(true)}>
      <Header
        title="Rules"
        subtitle={`${rules.length} configured`}
        action={<AppButton label="Add" icon={Plus} onPress={() => navigation.navigate("RuleEditor")} compact />}
      />
      <ErrorBanner message={error} />

      {rules.length ? (
        <View style={styles.list}>
          {rules.map((rule) => (
            <RuleCard
              key={rule.id}
              rule={rule}
              onEdit={() => navigation.navigate("RuleEditor", { id: rule.id })}
              onDelete={() => confirmDelete(rule)}
              onToggle={(enabled) => void toggleRule(rule, enabled)}
            />
          ))}
        </View>
      ) : (
        <EmptyState title="No rules configured." />
      )}

      <AppButton label="Refresh" icon={RefreshCcw} onPress={() => void load(true)} variant="secondary" />
    </Screen>
  );
}

function RuleCard({
  rule,
  onEdit,
  onDelete,
  onToggle
}: {
  rule: Rule;
  onEdit: () => void;
  onDelete: () => void;
  onToggle: (enabled: boolean) => void;
}) {
  return (
    <Card style={styles.card}>
      <View style={styles.topRow}>
        <View style={styles.titleGroup}>
          <Text style={styles.name} numberOfLines={1}>{rule.name}</Text>
          <Text style={styles.entity} numberOfLines={1}>{rule.entityId || "No device"}</Text>
        </View>
        <View style={styles.iconActions}>
          <IconButton icon={Edit3} onPress={onEdit} color={colors.blue} />
          <IconButton icon={Trash2} onPress={onDelete} color={colors.red} />
        </View>
      </View>

      <View style={styles.chips}>
        <StatusPill label={rule.currentState ? "ON" : "OFF"} tone={rule.currentState ? "success" : "neutral"} />
        {rule.useSolarProductionThreshold ? <StatusPill label={`PV >= ${formatWatts(rule.minAverageSolarProductionWatts)}`} tone="warning" /> : null}
        {rule.activeFrom && rule.activeTo ? <StatusPill label={`${rule.activeFrom} - ${rule.activeTo}`} tone="info" /> : null}
      </View>

      <View style={styles.details}>
        <Detail label="Turn ON" value={`SOC >= ${rule.socTurnOnThreshold}%`} />
        <Detail label="Turn OFF" value={`SOC <= ${rule.socTurnOffThreshold}%`} />
        <Detail label="Cooldown" value={`${rule.cooldownMinutes} min`} />
        <Detail label="Interval" value={`${rule.intervalSeconds}s`} />
      </View>

      <Text style={styles.timestamp}>Changed {formatDateTime(rule.currentStateChangedAt)}</Text>
      <SwitchRow title="Enabled" value={rule.enabled} onValueChange={onToggle} />
    </Card>
  );
}

function Detail({ label, value }: { label: string; value: string }) {
  return (
    <View style={styles.detail}>
      <Text style={styles.detailLabel}>{label}</Text>
      <Text style={styles.detailValue}>{value}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  list: {
    gap: spacing.md
  },
  card: {
    gap: spacing.lg
  },
  topRow: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: spacing.md
  },
  titleGroup: {
    flex: 1,
    gap: spacing.xs
  },
  name: {
    color: colors.text,
    fontSize: typography.section,
    fontWeight: "800"
  },
  entity: {
    color: colors.muted,
    fontSize: typography.caption
  },
  iconActions: {
    flexDirection: "row",
    gap: spacing.sm
  },
  chips: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: spacing.sm
  },
  details: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: spacing.sm
  },
  detail: {
    width: "48%",
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: colors.border,
    backgroundColor: colors.surfaceRaised,
    borderRadius: 8,
    padding: spacing.md,
    gap: spacing.xs
  },
  detailLabel: {
    color: colors.muted,
    fontSize: typography.caption,
    fontWeight: "700",
    textTransform: "uppercase"
  },
  detailValue: {
    color: colors.text,
    fontSize: typography.body,
    fontWeight: "700"
  },
  timestamp: {
    color: colors.muted,
    fontSize: typography.caption
  }
});
