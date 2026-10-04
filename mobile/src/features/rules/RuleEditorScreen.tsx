import { useDemoDisplayName } from "../demo/useDemoDisplayName";
import { translate as t } from "../../core/i18n";
import { useLanguage } from "../../application/LanguageContext";
import { useCallback, useEffect, useRef, useState } from "react";
import { Keyboard, Pressable, StyleSheet, Text, View } from "react-native";
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
import type { IntegrationSourceInverter } from "../../core/api/IntegrationApi";
import { colors, spacing, typography } from "../../core/theme";
import { useAuth } from "../../application/AuthContext";
import { RulesStackParamList } from "../../application/navigationTypes";

type Props = NativeStackScreenProps<RulesStackParamList, "RuleEditor">;

const defaultRule: RuleRequest = {
  name: "",
  entityId: "",
  sourceInverterId: null,
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
  const demoDisplayName = useDemoDisplayName();
  const { t } = useLanguage();
  const { api, isDemo } = useAuth();
  const ruleId = route.params?.id;
  const [rule, setRule] = useState<RuleRequest>(defaultRule);
  const [devices, setDevices] = useState<Device[]>([]);
  const [inverters, setInverters] = useState<IntegrationSourceInverter[]>([]);
  const [sourceError, setSourceError] = useState<string | null>(null);
  const [loadingSources, setLoadingSources] = useState(false);
  const [loading, setLoading] = useState(Boolean(ruleId));
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const savingPending = useRef(false);

  const loadSources = useCallback(async (signal?: AbortSignal) => {
    if (isDemo) return;
    setLoadingSources(true);
    try {
      const sources = await api.integrations.getSocketSources(signal);
      if (signal?.aborted) return;
      if (!Array.isArray(sources)) throw new Error("Unable to load inverter sources.");
      setInverters(sources);
      setSourceError(null);
    } catch (ex) {
      if (!signal?.aborted) setSourceError(ex instanceof Error ? ex.message : t("Unable to load inverter sources."));
    } finally {
      if (!signal?.aborted) setLoadingSources(false);
    }
  }, [api, isDemo]);

  const load = useCallback(async (signal?: AbortSignal) => {
    setLoading(true);
    setError(null);
    try {
      const [deviceList, loadedRule] = await Promise.all([
        api.getDevices(false),
        ruleId ? api.getRule(ruleId) : Promise.resolve(null)
      ]);
      if (signal?.aborted) return;
      setDevices(deviceList.devices);
      if (loadedRule) {
        setRule(toRequest(loadedRule));
      }
      await loadSources(signal);
    } catch (ex) {
      if (!signal?.aborted) setError(ex instanceof Error ? ex.message : "Unable to load rule.");
    } finally {
      if (!signal?.aborted) setLoading(false);
    }
  }, [api, loadSources, ruleId]);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  function setField<K extends keyof RuleRequest>(key: K, value: RuleRequest[K]) {
    setRule((current) => ({ ...current, [key]: value }));
  }

  function setNumberField<K extends keyof RuleRequest>(key: K, value: string) {
    const parsed = Number.parseInt(value, 10);
    setField(key, (Number.isNaN(parsed) ? 0 : parsed) as RuleRequest[K]);
  }

  const unknownSelection = Boolean(rule.entityId) && !devices.some((device) => device.id === rule.entityId);
  const unknownSource = Boolean(rule.sourceInverterId) && !inverters.some(inverter => inverter.id === rule.sourceInverterId);

  async function save() {
    if (savingPending.current) return;
    const message = validate(rule);
    if (message) {
      setError(message);
      return;
    }
    if (!isDemo && rule.enabled && rule.sourceInverterId && (sourceError || unknownSource)) {
      setError(sourceError ? "Inverter sources are unavailable. Reload sources before enabling this rule."
        : "Select an available source inverter.");
      return;
    }

    savingPending.current = true;
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
      savingPending.current = false;
      setSaving(false);
    }
  }

  if (loading) {
    return (
      <Screen scroll={false}>
        <LoadingState label={t("Loading rule...")} />
      </Screen>
    );
  }

  return (
    <Screen>
      <Header title={ruleId ? t("Edit Rule") : t("New Rule")} subtitle={rule.enabled ? t("Enabled") : t("Disabled")} />
      <ErrorBanner message={error} />

      <Card style={styles.form}>
        <TextField label={t("Rule name")} value={demoDisplayName(rule.name)} onChangeText={(value) => setField("name", value)} />
        <SwitchRow
          title={t("Enabled")}
          value={rule.enabled}
          disabled={!rule.entityId}
          onValueChange={(value) => setField("enabled", value)}
          subtitle={rule.entityId ? undefined : t("Select a device before enabling")}
        />
      </Card>

      <SectionTitle title={t("Target Device")} />
      {devices.length || unknownSelection ? (
        <View style={styles.deviceList}>
          {unknownSelection ? (
            <Pressable accessibilityRole="button" accessibilityLabel={rule.entityId}
              accessibilityState={{ selected: true, disabled: true }} disabled
              style={[styles.deviceChoice, styles.deviceChoiceSelected]}>
              <View style={styles.deviceCopy}>
                <Text style={styles.deviceName} numberOfLines={1}>{rule.entityId}</Text>
                <Text style={styles.deviceCategory}>{t("Unknown device")}</Text>
              </View>
              <StatusPill label={t("Selected")} tone="info" />
            </Pressable>
          ) : null}
          {devices.map((device) => (
            <Pressable
              key={device.id}
              accessibilityRole="button"
              accessibilityLabel={demoDisplayName(device.name)}
              accessibilityHint={`${t(device.category ?? device.id)}, ${!device.online ? t("Offline") : device.stateKnown === false ? t("State unavailable") : device.isOn ? t("ON") : t("OFF")}`}
              accessibilityState={{ selected: rule.entityId === device.id }}
              onPress={() => { Keyboard.dismiss(); setField("entityId", device.id); }}
              style={[styles.deviceChoice, rule.entityId === device.id && styles.deviceChoiceSelected]}
            >
              <View style={styles.deviceCopy}>
                <Text style={styles.deviceName} numberOfLines={1}>{demoDisplayName(device.name)}</Text>
                <Text style={styles.deviceCategory}>{t(device.category ?? device.id)}</Text>
              </View>
              <StatusPill label={!device.online ? t("Offline") : device.stateKnown === false ? t("State unavailable") : device.isOn ? t("ON") : t("OFF")} tone={!device.online || device.stateKnown === false ? "neutral" : device.isOn ? "success" : "warning"} />
            </Pressable>
          ))}
        </View>
      ) : (
        <EmptyState title={t("No devices loaded.")} />
      )}

      <SectionTitle title={t("Source inverter")} />
      <Card style={styles.form}>
        <ErrorBanner message={sourceError} />
        <AppButton label={isDemo ? t("Demo inverter") : t("Socket-linked or installation default inverter")} variant={!rule.sourceInverterId ? "primary" : "secondary"}
          disabled={saving} onPress={() => setField("sourceInverterId", null)} />
        {unknownSource ? <Text style={styles.deviceCategory}>{t("{0}: selected source is unavailable. Its reference is preserved.", rule.sourceInverterId)}</Text> : null}
        {inverters.map(inverter => <AppButton key={inverter.id} translateLabel={false} label={inverter.name}
          variant={rule.sourceInverterId === inverter.id ? "primary" : "secondary"} disabled={saving}
          onPress={() => setField("sourceInverterId", inverter.id)} />)}
        {!isDemo ? <AppButton label={t("Reload inverter sources")} variant="secondary" disabled={saving}
          onPress={() => void loadSources()} loading={loadingSources} /> : null}
        <Text style={styles.deviceCategory}>{t("Battery and PV conditions use the selected source. The default follows this socket's linked inverter, or the installation inverter when no link is set.")}</Text>
      </Card>

      <SectionTitle title={t("Turn Conditions")} />
      <Card style={styles.form}>
        <TextField
          label={t("SOC turn ON")}
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
          title={t("Separate turn OFF SOC")}
          value={rule.useSeparateSocTurnOffThreshold}
          onValueChange={(value) => {
            setField("useSeparateSocTurnOffThreshold", value);
            if (!value) {
              setField("socTurnOffThreshold", rule.socTurnOnThreshold);
            }
          }}
        />
        <TextField
          label={t("SOC turn OFF")}
          value={String(rule.socTurnOffThreshold)}
          onChangeText={(value) => setNumberField("socTurnOffThreshold", value)}
          keyboardType="number-pad"
          editable={rule.useSeparateSocTurnOffThreshold}
        />
        <SwitchRow
          title={t("Require average PV")}
          subtitle={t("Checked only while battery SOC is below 95%; bypassed at 95% or above")}
          value={rule.useSolarProductionThreshold}
          onValueChange={(value) => {
            setField("useSolarProductionThreshold", value);
            if (value && rule.minAverageSolarProductionWatts <= 0) {
              setField("minAverageSolarProductionWatts", 3000);
            }
          }}
        />
        <TextField
          label={t("Average PV last hour (W)")}
          value={String(rule.minAverageSolarProductionWatts)}
          onChangeText={(value) => setNumberField("minAverageSolarProductionWatts", value)}
          keyboardType="number-pad"
          editable={rule.useSolarProductionThreshold}
        />
      </Card>

      <SectionTitle title={t("Evaluation")} />
      <Card style={styles.form}>
        <TextField
          label={t("Cooldown minutes")}
          value={String(rule.cooldownMinutes)}
          onChangeText={(value) => setNumberField("cooldownMinutes", value)}
          keyboardType="number-pad"
        />
        <TextField
          label={t("Interval seconds")}
          value={String(rule.intervalSeconds)}
          onChangeText={(value) => setNumberField("intervalSeconds", value)}
          keyboardType="number-pad"
        />
        <View style={styles.timeRow}>
          <View style={styles.timeField}>
            <TextField
              label={t("Active from")}
              value={rule.activeFrom ?? ""}
              onChangeText={(value) => setField("activeFrom", value || null)}
              placeholder="HH:mm"
            />
          </View>
          <View style={styles.timeField}>
            <TextField
              label={t("Active to")}
              value={rule.activeTo ?? ""}
              onChangeText={(value) => setField("activeTo", value || null)}
              placeholder="HH:mm"
            />
          </View>
        </View>
      </Card>

      <AppButton label={t("Save")} icon={Save} onPress={() => void save()} loading={saving} />
    </Screen>
  );
}

function toRequest(rule: Rule): RuleRequest {
  return {
    configurationVersion: rule.configurationVersion,
    name: rule.name,
    entityId: rule.entityId,
    sourceInverterId: rule.sourceInverterId ?? null,
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
    activeFrom: rule.activeFrom?.trim() || null,
    activeTo: rule.activeTo?.trim() || null
  };
}

function validate(rule: RuleRequest): string | null {
  if (!rule.name.trim()) {
    return t("Rule name is required.");
  }

  if (!rule.entityId.trim()) {
    return t("Select a target device.");
  }

  if (rule.socTurnOnThreshold < 0 || rule.socTurnOnThreshold > 100) {
    return t("SOC turn ON must be between 0 and 100%.");
  }

  if (rule.useSeparateSocTurnOffThreshold) {
    if (rule.socTurnOffThreshold < 0 || rule.socTurnOffThreshold > 100) {
      return t("SOC turn OFF must be between 0 and 100%.");
    }

    if (rule.socTurnOffThreshold > rule.socTurnOnThreshold) {
      return t("SOC turn OFF cannot be higher than SOC turn ON.");
    }
  }

  if (rule.useSolarProductionThreshold &&
      (rule.minAverageSolarProductionWatts < 1 || rule.minAverageSolarProductionWatts > 30000)) {
    return t("Average PV threshold must be between 1 and 30000 W.");
  }

  if (rule.cooldownMinutes < 1 || rule.cooldownMinutes > 240) {
    return t("Cooldown must be between 1 and 240 minutes.");
  }

  if (rule.intervalSeconds < 10 || rule.intervalSeconds > 3600) {
    return t("Interval must be between 10 and 3600 seconds.");
  }

  if (Boolean(rule.activeFrom) !== Boolean(rule.activeTo)) {
    return t("Set both time-window values or leave both empty.");
  }

  const timePattern = /^([01]\d|2[0-3]):[0-5]\d$/;
  if ((rule.activeFrom && !timePattern.test(rule.activeFrom)) ||
      (rule.activeTo && !timePattern.test(rule.activeTo))) {
    return t("Time window values must use HH:mm.");
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
