import { useDemoDisplayName } from "../demo/useDemoDisplayName";
import { useLanguage } from "../../application/LanguageContext";
import { useCallback, useState } from "react";
import { Alert, StyleSheet, Text, View } from "react-native";
import { useFocusEffect } from "@react-navigation/native";
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
import { formatDateTime, formatNumber, formatWatts } from "../../core/format";
import { colors, spacing, typography } from "../../core/theme";
import { useAuth } from "../../application/AuthContext";
import { RulesStackParamList } from "../../application/navigationTypes";

type Props = NativeStackScreenProps<RulesStackParamList, "RulesList">;

export function RulesScreen({ navigation }: Props) {
  const { t } = useLanguage();
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

  useFocusEffect(useCallback(() => {
    void load(false);
  }, [load]));

  async function toggleRule(rule: Rule, enabled: boolean) {
    setError(null);
    setRules((current) => current.map((item) => (item.id === rule.id ? { ...item, enabled } : item)));
    try {
      const updated = await api.setRuleEnabled(rule.id, enabled);
      setRules((current) => current.map((item) => (item.id === rule.id ? updated : item)));
      if (enabled && !updated.enabled) {
        setError("Select a device before enabling this rule.");
      }
    } catch (ex) {
      setRules((current) => current.map((item) => (item.id === rule.id ? rule : item)));
      setError(ex instanceof Error ? ex.message : "Unable to update rule.");
    }
  }

  function confirmDelete(rule: Rule) {
    Alert.alert(t("Delete rule"), rule.name, [
      { text: t("Cancel"), style: "cancel" },
      {
        text: t("Delete"),
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
        <LoadingState label={t("Loading rules...")} />
      </Screen>
    );
  }

  return (
    <Screen refreshing={refreshing} onRefresh={() => void load(true)}>
      <Header
        title={t("Rules")}
        subtitle={t("{0} configured", rules.length)}
        action={<AppButton label={t("Add")} icon={Plus} onPress={() => navigation.navigate("RuleEditor")} compact />}
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
        <EmptyState title={t("No rules configured.")} />
      )}

      <AppButton label={t("Refresh")} icon={RefreshCcw} onPress={() => void load(true)} variant="secondary" />
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
  const demoDisplayName = useDemoDisplayName();
  const { t } = useLanguage();
  return (
    <Card style={styles.card}>
      <View style={styles.topRow}>
        <View style={styles.titleGroup}>
          <Text style={styles.name} numberOfLines={1}>{demoDisplayName(rule.name)}</Text>
          <Text style={styles.entity} numberOfLines={1}>{rule.entityId || t("No device")}</Text>
        </View>
        <View style={styles.iconActions}>
          <IconButton accessibilityLabel={t("Edit rule {0}", rule.name)} icon={Edit3} onPress={onEdit} color={colors.blue} />
          <IconButton accessibilityLabel={t("Delete rule {0}", rule.name)} icon={Trash2} onPress={onDelete} color={colors.red} />
        </View>
      </View>

      <View style={styles.chips}>
        <StatusPill label={rule.currentState ? t("ON") : t("OFF")} tone={rule.currentState ? "success" : "neutral"} />
        {rule.useSolarProductionThreshold ? <StatusPill label={t("PV >= {0} (SOC < 95%)", formatWatts(rule.minAverageSolarProductionWatts))} tone="warning" /> : null}
        {rule.activeFrom && rule.activeTo ? <StatusPill label={`${rule.activeFrom} - ${rule.activeTo}`} tone="info" /> : null}
      </View>

      <View style={styles.details}>
        <Detail label={t("Turn ON")} value={`SOC >= ${formatNumber(rule.socTurnOnThreshold)}%`} />
        <Detail label={t("Turn OFF")} value={`SOC <= ${formatNumber(rule.socTurnOffThreshold)}%`} />
        <Detail label={t("Cooldown")} value={`${formatNumber(rule.cooldownMinutes)} min`} />
        <Detail label={t("Interval")} value={`${formatNumber(rule.intervalSeconds)}s`} />
      </View>

      <Text style={styles.timestamp}>{t("Changed {0}", formatDateTime(rule.currentStateChangedAt))}</Text>
      <SwitchRow
        title={t("Enabled")}
        value={rule.enabled}
        onValueChange={onToggle}
        disabled={!rule.entityId}
        subtitle={rule.entityId ? undefined : t("Select a device before enabling")}
      />

    </Card>
  );
}

function Detail({ label, value }: { label: string; value: string }) {
  const { t } = useLanguage();
  return (
    <View style={styles.detail}>
      <Text style={styles.detailLabel}>{t(label)}</Text>
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
