import { useLanguage } from "../../application/LanguageContext";
import { Pressable, StyleSheet, View } from "react-native";
import { ThemedText as Text } from "../../core/components";
import { useLegacyTheme } from "../../ui/theme/ThemeProvider";
import { colors, spacing } from "../../core/theme";

export function PeriodNavigation({ caption, previous, next, onPrevious, onNext, onToday }: {
  caption: string; previous: boolean; next: boolean; onPrevious: () => void; onNext: () => void; onToday: () => void;
}) {
  const { t } = useLanguage(); const { colors } = useLegacyTheme(); const styles = makeStyles(colors);
  return <View style={styles.row}>
    <Pressable accessibilityRole="button" accessibilityLabel={t("Previous period")} disabled={!previous} onPress={onPrevious} style={[styles.arrow, !previous && styles.disabled]}><Text style={styles.arrowText}>‹</Text></Pressable>
    <Text style={styles.caption}>{caption}</Text>
    <Pressable accessibilityRole="button" accessibilityLabel={t("Next period")} disabled={!next} onPress={onNext} style={[styles.arrow, !next && styles.disabled]}><Text style={styles.arrowText}>›</Text></Pressable>
    <Pressable accessibilityRole="button" onPress={onToday} style={styles.today}><Text style={styles.link}>{t("Today")}</Text></Pressable>
  </View>;
}

export const energyStyles = StyleSheet.create({
  card: { gap: spacing.md }, heading: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: spacing.sm },
  title: { fontSize: 18, fontWeight: "700", color: colors.text, flex: 1 }, link: { color: colors.primary, fontSize: 12, fontWeight: "700", paddingVertical: 10 },
  muted: { color: colors.muted, fontSize: 12, lineHeight: 18 }, warning: { color: colors.amber, fontSize: 12, lineHeight: 18 },
  metrics: { flexDirection: "row", gap: spacing.lg }, metric: { flex: 1, gap: 6 }, metricValue: { color: colors.text, fontSize: 22, fontWeight: "700", fontVariant: ["tabular-nums"] },
  primaryValue: { color: colors.primary }, amberValue: { color: colors.amber }, legend: { flexDirection: "row", gap: spacing.lg, flexWrap: "wrap" },
  provisional: { borderWidth: 1, borderStyle: "dashed", borderColor: colors.primaryDark, borderRadius: 8, padding: spacing.md, gap: 5 },
  divider: { borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: colors.border, paddingTop: spacing.md, gap: spacing.sm }
});

const makeStyles = (colors: ReturnType<typeof useLegacyTheme>["colors"]) => StyleSheet.create({
  row: { flexDirection: "row", alignItems: "center", gap: 4 }, caption: { flex: 1, color: colors.text, fontSize: 13, textAlign: "center", fontWeight: "600" },
  arrow: { width: 36, minHeight: 44, justifyContent: "center", alignItems: "center" }, arrowText: { fontSize: 28, color: colors.text }, disabled: { opacity: .25 },
  today: { padding: 10 }, link: { fontSize: 12, color: colors.primary, fontWeight: "700" }
});
