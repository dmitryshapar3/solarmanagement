import { StyleSheet, Text, View } from "react-native";
import { ArrowRight, RefreshCcw } from "lucide-react-native";
import { AppButton } from "./components";
import { colors, spacing, typography } from "./theme";

export function TileHeader({ title, subtitle, loading, onRefresh, onDetails, accessibilityScope }: {
  title: string;
  subtitle?: string;
  loading?: boolean;
  onRefresh: () => void;
  onDetails?: () => void;
  accessibilityScope?: string;
}) {
  const scope = accessibilityScope ?? title;
  return <View style={styles.header}>
    <View style={styles.copy}>
      <Text style={styles.title}>{title}</Text>
      {subtitle ? <Text style={styles.subtitle}>{subtitle}</Text> : null}
    </View>
    <View style={styles.actions}>
      <AppButton label="Refresh" accessibilityLabel={`${scope} refresh`} icon={RefreshCcw} onPress={onRefresh} loading={loading} variant="secondary" compact />
      {onDetails ? <AppButton label="Details" accessibilityLabel={`${scope} details`} icon={ArrowRight} onPress={onDetails} variant="secondary" compact /> : null}
    </View>
  </View>;
}

const styles = StyleSheet.create({
  header: { gap: spacing.md },
  copy: { gap: spacing.xs },
  title: { color: colors.text, fontSize: typography.section, fontWeight: "700" },
  subtitle: { color: colors.muted, fontSize: typography.caption, lineHeight: 18 },
  actions: { flexDirection: "row", flexWrap: "wrap", gap: spacing.sm }
});
