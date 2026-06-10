import { useCallback, useEffect, useState } from "react";
import { Pressable, StyleSheet, Text, View } from "react-native";
import { NativeStackScreenProps } from "@react-navigation/native-stack";
import { Save } from "lucide-react-native";
import {
  AppButton,
  Card,
  EmptyState,
  ErrorBanner,
  Header,
  LoadingState,
  Screen,
  SectionTitle,
  StatusPill,
  SwitchRow,
  TextField
} from "../../core/components";
import { Device, Rule, RuleRequest } from "../../core/api/types";
import { colors, spacing, typography } from "../../core/theme";
import { useAuth } from "../../application/AuthContext";
import { RulesStackParamList } from "../../application/navigationTypes";

type Props = NativeStackScreenProps<RulesStackParamList, "RuleEditor">;

const defaultRule: RuleRequest = {
  name: "",
  entityId: "",
  enabled: false,
  socTurnOnThreshold: 80,
  useSeparateSocTurnOffThreshold: false,
  socTurnOffThreshold: 80,
  useSolarProductionThreshold: false,
  minAverageSolarProductionWatts: 3000,
  cooldownMinutes: 15,
  intervalSeconds: 30,
  activeFrom: null,
  activeTo: null
};

export function RuleEditorScreen({ route, navigation }: Props) {
  const { api } = useAuth();
  const ruleId = route.params?.id;
  const [rule, setRule] = useState<RuleRequest>(defaultRule);
  const [devices, setDevices] = useState<Device[]>([]);
  const [loading, setLoading] = useState(Boolean(ruleId));
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [deviceList, loadedRule] = await Promise.all([
        api.getDevices(false),
        ruleId ? api.getRule(ruleId) : Promise.resolve(null)
      ]);
      setDevices(deviceList);
      if (loadedRule) {
        setRule(toRequest(loadedRule));
      }
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Unable to load rule.");
    } finally {
      setLoading(false);
    }
  }, [api, ruleId]);

  useEffect(() => {
    void load();
  }, [load]);

  function setField<K extends keyof RuleRequest>(key: K, value: RuleRequest[K]) {
    setRule((current) => ({ ...current, [key]: value }));
  }

  function setNumberField<K extends keyof RuleRequest>(key: K, value: string) {
    const parsed = Number.parseInt(value, 10);
    setField(key, (Number.isNaN(parsed) ? 0 : parsed) as RuleRequest[K]);
  }

  async function save() {
    const message = validate(rule);
    if (message) {
      setError(message);
      return;
    }

    setSaving(true);
    setError(null);
    try {
      const payload = normalizeRule(rule);
      if (ruleId) {
        await api.updateRule(ruleId, payload);
      } else {
        await api.createRule(payload);
      }
      navigation.goBack();
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Unable to save rule.");
    } finally {
      setSaving(false);
    }
  }

  if (loading) {
    return (
      <Screen scroll={false}>
        <LoadingState label="Loading rule..." />
      </Screen>
    );
  }

  return (
    <Screen>
      <Header title={ruleId ? "Edit Rule" : "New Rule"} subtitle={rule.enabled ? "Enabled" : "Disabled"} />
      <ErrorBanner message={error} />

      <Card style={styles.form}>
        <TextField label="Rule name" value={rule.name} onChangeText={(value) => setField("name", value)} />
        <SwitchRow
          title="Enabled"
          value={rule.enabled}
          disabled={!rule.entityId}
          onValueChange={(value) => setField("enabled", value)}
          subtitle={rule.entityId ? undefined : "Select a device before enabling"}
        />
      </Card>

      <SectionTitle title="Target Device" />
      {devices.length ? (
        <View style={styles.deviceList}>
          {devices.map((device) => (
            <Pressable
              key={device.id}
              onPress={() => setField("entityId", device.id)}
              style={[styles.deviceChoice, rule.entityId === device.id && styles.deviceChoiceSelected]}
            >
              <View style={styles.deviceCopy}>
                <Text style={styles.deviceName} numberOfLines={1}>{device.name}</Text>
                <Text style={styles.deviceCategory}>{device.category ?? device.id}</Text>
              </View>
              <StatusPill label={!device.online ? "Offline" : device.isOn ? "ON" : "OFF"} tone={!device.online ? "neutral" : device.isOn ? "success" : "warning"} />
            </Pressable>
          ))}
        </View>
      ) : (
        <EmptyState title="No devices loaded." />
      )}

      <SectionTitle title="Turn Conditions" />
      <Card style={styles.form}>
        <TextField
          label="SOC turn ON"
          value={String(rule.socTurnOnThreshold)}
          onChangeText={(value) => {
            setNumberField("socTurnOnThreshold", value);
            if (!rule.useSeparateSocTurnOffThreshold) {
              setNumberField("socTurnOffThreshold", value);
            }
          }}
          keyboardType="number-pad"
        />
        <SwitchRow
          title="Separate turn OFF SOC"
          value={rule.useSeparateSocTurnOffThreshold}
          onValueChange={(value) => {
            setField("useSeparateSocTurnOffThreshold", value);
            if (!value) {
              setField("socTurnOffThreshold", rule.socTurnOnThreshold);
            }
          }}
        />
        <TextField
          label="SOC turn OFF"
          value={String(rule.socTurnOffThreshold)}
          onChangeText={(value) => setNumberField("socTurnOffThreshold", value)}
          keyboardType="number-pad"
        />
        <SwitchRow
          title="Require average PV"
          value={rule.useSolarProductionThreshold}
          onValueChange={(value) => setField("useSolarProductionThreshold", value)}
        />
        <TextField
          label="Average PV watts"
          value={String(rule.minAverageSolarProductionWatts)}
          onChangeText={(value) => setNumberField("minAverageSolarProductionWatts", value)}
          keyboardType="number-pad"
        />
      </Card>

      <SectionTitle title="Evaluation" />
      <Card style={styles.form}>
        <TextField
          label="Cooldown minutes"
          value={String(rule.cooldownMinutes)}
          onChangeText={(value) => setNumberField("cooldownMinutes", value)}
          keyboardType="number-pad"
        />
        <TextField
          label="Interval seconds"
          value={String(rule.intervalSeconds)}
          onChangeText={(value) => setNumberField("intervalSeconds", value)}
          keyboardType="number-pad"
        />
        <View style={styles.timeRow}>
          <View style={styles.timeField}>
            <TextField
              label="Active from"
              value={rule.activeFrom ?? ""}
              onChangeText={(value) => setField("activeFrom", value || null)}
              placeholder="HH:mm"
            />
          </View>
          <View style={styles.timeField}>
            <TextField
              label="Active to"
              value={rule.activeTo ?? ""}
              onChangeText={(value) => setField("activeTo", value || null)}
              placeholder="HH:mm"
            />
          </View>
        </View>
      </Card>

      <AppButton label="Save" icon={Save} onPress={() => void save()} loading={saving} />
    </Screen>
  );
}

function toRequest(rule: Rule): RuleRequest {
  return {
    name: rule.name,
    entityId: rule.entityId,
    enabled: rule.enabled,
    socTurnOnThreshold: rule.socTurnOnThreshold,
    useSeparateSocTurnOffThreshold: rule.useSeparateSocTurnOffThreshold,
    socTurnOffThreshold: rule.socTurnOffThreshold,
    useSolarProductionThreshold: rule.useSolarProductionThreshold,
    minAverageSolarProductionWatts: rule.minAverageSolarProductionWatts,
    cooldownMinutes: rule.cooldownMinutes,
    intervalSeconds: rule.intervalSeconds,
    activeFrom: rule.activeFrom,
    activeTo: rule.activeTo
  };
}

function normalizeRule(rule: RuleRequest): RuleRequest {
  return {
    ...rule,
    name: rule.name.trim(),
    entityId: rule.entityId.trim(),
    enabled: Boolean(rule.entityId) && rule.enabled,
    socTurnOffThreshold: rule.useSeparateSocTurnOffThreshold ? rule.socTurnOffThreshold : rule.socTurnOnThreshold,
    minAverageSolarProductionWatts: rule.useSolarProductionThreshold ? rule.minAverageSolarProductionWatts : 3000,
    activeFrom: rule.activeFrom?.trim() || null,
    activeTo: rule.activeTo?.trim() || null
  };
}

function validate(rule: RuleRequest): string | null {
  if (!rule.name.trim()) {
    return "Rule name is required.";
  }

  if (rule.socTurnOnThreshold < 0 || rule.socTurnOnThreshold > 100) {
    return "SOC turn ON must be between 0 and 100%.";
  }

  if (rule.useSeparateSocTurnOffThreshold) {
    if (rule.socTurnOffThreshold < 0 || rule.socTurnOffThreshold > 100) {
      return "SOC turn OFF must be between 0 and 100%.";
    }

    if (rule.socTurnOffThreshold > rule.socTurnOnThreshold) {
      return "SOC turn OFF cannot be higher than SOC turn ON.";
    }
  }

  if (rule.useSolarProductionThreshold &&
      (rule.minAverageSolarProductionWatts < 1 || rule.minAverageSolarProductionWatts > 30000)) {
    return "Average PV threshold must be between 1 and 30000 W.";
  }

  if (rule.cooldownMinutes < 1 || rule.cooldownMinutes > 240) {
    return "Cooldown must be between 1 and 240 minutes.";
  }

  if (rule.intervalSeconds < 10 || rule.intervalSeconds > 3600) {
    return "Interval must be between 10 and 3600 seconds.";
  }

  if (Boolean(rule.activeFrom) !== Boolean(rule.activeTo)) {
    return "Set both time-window values or leave both empty.";
  }

  const timePattern = /^([01]\d|2[0-3]):[0-5]\d$/;
  if ((rule.activeFrom && !timePattern.test(rule.activeFrom)) ||
      (rule.activeTo && !timePattern.test(rule.activeTo))) {
    return "Time window values must use HH:mm.";
  }

  return null;
}

const styles = StyleSheet.create({
  form: {
    gap: spacing.lg
  },
  deviceList: {
    gap: spacing.sm
  },
  deviceChoice: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: spacing.md,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: colors.border,
    borderRadius: 8,
    backgroundColor: colors.surface,
    padding: spacing.md
  },
  deviceChoiceSelected: {
    borderColor: colors.primary,
    backgroundColor: colors.surfaceRaised
  },
  deviceCopy: {
    flex: 1,
    gap: spacing.xs
  },
  deviceName: {
    color: colors.text,
    fontSize: typography.body,
    fontWeight: "800"
  },
  deviceCategory: {
    color: colors.muted,
    fontSize: typography.caption
  },
  timeRow: {
    flexDirection: "row",
    gap: spacing.md
  },
  timeField: {
    flex: 1
  }
});
