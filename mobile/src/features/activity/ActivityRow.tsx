import { Pressable, View } from "react-native";
import { Check, Clock3, Pause, Power, TriangleAlert } from "lucide-react-native";
import { useLanguage } from "../../application/LanguageContext";
import type { ActivityItem } from "../../core/api/redesignTypes";
import { ThemedText as Text } from "../../core/components";
import { formatDateTime, formatNumber } from "../../core/format";
import { useTheme } from "../../ui/theme/ThemeProvider";
import { ruleDecisionLabel } from "../rules/rulePresentation";
import { useDemoDisplayName } from "../demo/useDemoDisplayName";

export function activityTitle(item: ActivityItem): string {
  if (item.kind === "command.manual") return item.state ? "Turn-on requested by hand" : "Turn-off requested by hand";
  if (item.kind === "command.result") return item.reasonCode === "acknowledged" ? "Provider acknowledged command" : item.reasonCode === "rejected" ? "Command rejected" : "Command result unknown";
  if (item.kind === "rule.paused") return "Automation paused";
  if (item.kind === "rule.resumed") return "Automation resumed";
  if (item.kind === "rule.created") return "Automation created";
  if (item.kind === "rule.updated") return "Automation edited";
  if (item.kind === "rule.deleted") return "Automation deleted";
  if (item.kind === "rule.switched") return item.state ? "Automation requested turn-on" : "Automation requested turn-off";
  if (item.kind === "rule.failed") return "Automation check failed";
  if (item.kind === "rule.checked") return "Conditions checked";
  if (item.kind === "device.observed") return "Device state observed";
  return "Activity event";
}
export function ActivityRow({ item, onPress }: { item: ActivityItem; onPress?: () => void }) {
  const { colors } = useTheme(); const { t } = useLanguage(); const name = useDemoDisplayName();
  const Icon = item.kind === "rule.checked" ? Check : item.kind.includes("paused") ? Pause : item.kind === "command.manual" ? Clock3 : item.kind === "rule.failed" || item.reasonCode === "uncertain" ? TriangleAlert : Power;
  return <Pressable accessibilityRole={onPress ? "button" : undefined} onPress={onPress} disabled={!onPress} style={{ flexDirection: "row", gap: 12, paddingVertical: 14, alignItems: "flex-start", minHeight: 64 }}>
    <View style={{ width: 36, height: 36, borderRadius: 18, backgroundColor: colors.fill, alignItems: "center", justifyContent: "center" }}><Icon color={colors.ink2} size={18} /></View>
    <View style={{ flex: 1, gap: 3 }}><Text style={{ fontWeight: "600", fontSize: 15 }}>{t(activityTitle(item))}</Text>{item.ruleName ? <Text style={{ color: colors.ink2, fontSize: 13 }}>{name(item.ruleName)}</Text> : null}<Text style={{ color: colors.ink3, fontSize: 13 }}>{formatDateTime(item.start)}{item.end !== item.start ? `–${formatDateTime(item.end)}` : ""}{item.checkCount > 1 ? ` · ${t("{0} checks", item.checkCount)}` : ""}</Text>{item.checkCount > 0 ? <Text style={{ color: colors.ink2, fontSize: 13 }}>{t("Battery {0}–{1}% · Solar {2}–{3} kW", item.socMin === null ? "—" : formatNumber(item.socMin, 0), item.socMax === null ? "—" : formatNumber(item.socMax, 0), item.solarMinWatts === null ? "—" : formatNumber(item.solarMinWatts / 1000, 2), item.solarMaxWatts === null ? "—" : formatNumber(item.solarMaxWatts / 1000, 2))}</Text> : null}{item.reasonCode && item.kind.startsWith("rule.") ? <Text style={{color:colors.ink3,fontSize:13}}>{t(ruleDecisionLabel(item.reasonCode))}</Text> : null}{item.client === "mobile" || item.client === "web" ? <Text style={{color:colors.ink3,fontSize:13}}>{t(item.client === "mobile" ? "From the mobile app" : "From the website")}</Text> : null}</View>
  </Pressable>;
}
