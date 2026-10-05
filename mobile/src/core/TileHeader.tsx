import { useLanguage } from "../application/LanguageContext";
import { StyleSheet, Text, View } from "react-native";
import { ArrowRight } from "lucide-react-native";
import { AppButton } from "./components";
import { colors, spacing, typography } from "./theme";

export function TileHeader({ title, subtitle, onDetails, accessibilityScope }: {
  title: string;
  subtitle?: string;
  onDetails?: () => void;
  accessibilityScope?: string;
}) {
  const { t } = useLanguage();
  const scope = accessibilityScope ?? title;
  return <View style={styles.header}>
    <View style={styles.copy}>
      <Text style={styles.title}>{t(title)}</Text>
      {subtitle ? <Text style={styles.subtitle}>{t(subtitle)}</Text> : null}
    </View>
    {onDetails ? <View style={styles.actions}>
      <AppButton label={t("Details")} accessibilityLabel={t("{0} details", t(scope))} icon={ArrowRight} onPress={onDetails} variant="secondary" compact />
    </View> : null}
  </View>;
}

const styles = StyleSheet.create({
  header: { gap: spacing.md },
  copy: { gap: spacing.xs },
  title: { color: colors.text, fontSize: typography.section, fontWeight: "700" },
  subtitle: { color: colors.muted, fontSize: typography.caption, lineHeight: 18 },
  actions: { flexDirection: "row", flexWrap: "wrap", gap: spacing.sm }
});
