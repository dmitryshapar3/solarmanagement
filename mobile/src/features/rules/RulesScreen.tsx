import { useCallback, useState } from "react";
import { Pressable, View } from "react-native";
import { useNavigation } from "@react-navigation/native";
import type { NativeStackNavigationProp } from "@react-navigation/native-stack";
import { ChevronRight, Plus } from "lucide-react-native";
import { useAuth } from "../../application/AuthContext";
import { useLanguage } from "../../application/LanguageContext";
import type { RootStackParamList } from "../../application/navigationTypes";
import { useScopedAction } from "../../application/useScopedAction";
import type { Rule } from "../../core/api/types";
import type { RuleEvaluation } from "../../core/api/redesignTypes";
import { AppButton, Banner, Card, EmptyState, ErrorBanner, Header, LoadingState, NativeSwitch, Screen, StatusPill, ThemedText as Text } from "../../core/components";
import { formatDateTime, formatNumber, formatWatts } from "../../core/format";
import { useTheme } from "../../ui/theme/ThemeProvider";
import { useDemoDisplayName } from "../demo/useDemoDisplayName";
import { useFocusedResource } from "../energy/useFocusedResource";
import { ruleDecisionLabel } from "./rulePresentation";

export function RulesScreen() {
  const { api } = useAuth(); const { t } = useLanguage(); const { colors } = useTheme(); const name = useDemoDisplayName();
  const navigation = useNavigation<NativeStackNavigationProp<RootStackParamList>>(); const [error, setError] = useState<string | null>(null);
  const resource = useFocusedResource("automations", useCallback(async (signal: AbortSignal) => {
    const [rules, inventory, permissions] = await Promise.all([api.getRules(), api.getDevices(false, signal), api.accountSecurity.getPermissions(signal)]);
    const evaluations = await Promise.allSettled(rules.map(rule => api.getRuleEvaluation(rule.id, signal)));
    const states = new Map<number, RuleEvaluation>(); evaluations.forEach((value, index) => { if (value.status === "fulfilled") states.set(rules[index]!.id, value.value); });
    return { rules, inventory, states, canEdit: permissions.permissions.includes("ManageRules") };
  }, [api]), api);
  const actions = useScopedAction(api, "automations", () => setError(null));
  const toggle = (rule: Rule, enabled: boolean) => actions.run(`toggle:${rule.id}`, async context => {
    if (!rule.configurationVersion) throw new Error(t("The automation has changed. Pull down before editing."));
    const updated = await api.setRuleEnabled(rule.id, enabled, rule.configurationVersion);
    context.publish(() => { if (enabled && !updated.enabled) setError("Select a device before enabling this rule."); });
    if (context.isCurrent()) { resource.invalidate(); await resource.refresh(true); }
  }, { started: () => setError(null), failed: exception => setError(exception instanceof Error ? exception.message : "Unable to update rule.") });
  const data = resource.data; const rules = data?.rules ?? []; const enabled = rules.filter(rule => rule.enabled).length;
  return <Screen refreshing={resource.loading || actions.busy !== null} onRefresh={() => resource.refresh(true)}>
    <Header title="Automations" subtitle={t("{0} enabled · {1} paused", enabled, rules.length - enabled)} action={<AppButton label="Activity" variant="ghost" compact onPress={() => navigation.navigate("MainTabs", { screen: "Automations", params: { screen: "Activity" } })} />} />
    <ErrorBanner message={resource.error ?? error} />
    {data && !data.canEdit ? <Banner>{t("You can view automations. Ask the installation owner to change them.")}</Banner> : null}
    {resource.loading && !data ? <LoadingState label="Loading rules..." /> : !rules.length ? <EmptyState title="Use more of your solar" detail="An automation switches a plug when the battery and solar conditions are right." /> : rules.map(rule => {
      const device = data?.inventory.devices.find(device => device.id === rule.entityId); const evaluation = data?.states.get(rule.id); const pausedManually = !rule.enabled && rule.pauseReason === "manual_override";
      return <Card key={rule.id} style={{ gap: 14 }}><View style={{ flexDirection: "row", alignItems: "center", gap: 12 }}><Pressable accessibilityRole="button" accessibilityLabel={t("Edit automation {0}", name(rule.name))} onPress={() => navigation.navigate("AutomationEditor", { id: rule.id })} style={{ flex: 1, minHeight: 44, justifyContent: "center", gap: 4 }}><Text style={{ fontSize: 19, lineHeight: 24, fontWeight: "600" }}>{name(rule.name)}</Text><Text style={{ color: colors.ink3, fontSize: 13 }}>{device ? name(device.name) : t("Device unavailable")}</Text></Pressable><NativeSwitch label={t("Enable {0}", name(rule.name))} value={rule.enabled} disabled={!data?.canEdit || !rule.entityId || actions.busy !== null || resource.loading} onValueChange={value => void toggle(rule, value)} /></View>
        {pausedManually ? <StatusPill label="Paused when you switched by hand" tone="warning" /> : <StatusPill label={rule.enabled ? "Enabled" : "Paused"} />}
        <View style={{ flexDirection: "row", gap: 8, flexWrap: "wrap" }}><StatusPill label={t("On at {0}% · off below {1}%", rule.socTurnOnThreshold, rule.socTurnOffThreshold)} />{rule.useSolarProductionThreshold ? <StatusPill label={t("Solar at least {0}", formatWatts(rule.minAverageSolarProductionWatts))} /> : null}</View>
        <Text style={{ fontSize: 15, lineHeight: 21, color: colors.ink2 }}>{t("When the battery reaches {0}%, turn on {1}. Turn it off below {2}%.", formatNumber(rule.socTurnOnThreshold, 0), device ? name(device.name) : t("the selected device"), formatNumber(rule.socTurnOffThreshold, 0))}{rule.useSolarProductionThreshold ? ` ${t("Also require at least {0} average solar power.", formatWatts(rule.minAverageSolarProductionWatts))}` : ""}</Text><View style={{ flexDirection: "row", gap: 8, flexWrap: "wrap" }}><StatusPill label={t("Check every {0}s", rule.intervalSeconds)} /><StatusPill label={t("Wait {0} min between switches", rule.cooldownMinutes)} /></View>
        {rule.activeFrom && rule.activeTo ? <Text style={{ fontSize: 13, color: colors.ink3 }}>{t("Active {0}–{1}", rule.activeFrom, rule.activeTo)}</Text> : null}
        <View style={{ flexDirection: "row", alignItems: "center", gap: 8 }}><View style={{ flex: 1, gap: 3 }}><Text style={{ color: colors.ink2, fontSize: 13 }}>{t(ruleDecisionLabel(evaluation?.decision ?? "not_checked"))}</Text>{evaluation?.nextCheckAt ? <Text style={{ color: colors.ink3, fontSize: 13 }}>{t("Next check {0}", formatDateTime(evaluation.nextCheckAt))}</Text> : null}{evaluation?.checkedAt ? <Text style={{ color: colors.ink3, fontSize: 13 }}>{t("Checked {0}", formatDateTime(evaluation.checkedAt))}</Text> : null}{evaluation && evaluation.freshness !== "current" ? <Text style={{ color: colors.warningText, fontSize: 13 }}>{t(evaluation.freshness === "source_changed" || evaluation.freshness === "configuration_changed" ? "Configuration changed · waiting for fresh readings" : "Previous check · waiting for fresh readings")}</Text> : null}</View><AppButton label="Edit" variant="ghost" compact icon={ChevronRight} onPress={() => navigation.navigate("AutomationEditor", { id: rule.id })} /></View>
      </Card>;
    })}
    <Card style={{ gap: 8 }}><Text style={{ fontSize: 19, fontWeight: "600" }}>{t("Start with a template")}</Text>{([{ value: "solar", label: "Use solar surplus" }, { value: "reserve", label: "Keep a battery reserve" }, { value: "daylight", label: "Daylight only" }] as const).map(template => <AppButton key={template.value} label={template.label} variant="quiet" disabled={!data?.canEdit || resource.loading || actions.busy !== null} onPress={() => navigation.navigate("AutomationEditor", { template: template.value })} />)}<Text style={{ fontSize: 13, color: colors.ink3 }}>{t("Review the preset and choose a device before saving. Templates start paused.")}</Text></Card>
    <AppButton label="New automation" icon={Plus} disabled={!data?.canEdit || resource.loading || actions.busy !== null} onPress={() => navigation.navigate("AutomationEditor")} />
  </Screen>;
}
