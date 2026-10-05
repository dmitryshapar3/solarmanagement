import { useState } from "react";
import { Pressable, ScrollView, StyleSheet, Text, View } from "react-native";
import { Check, ChevronDown } from "lucide-react-native";
import { useLanguage } from "../../application/LanguageContext";
import { colors, spacing, typography } from "../../core/theme";

export function LanguageDropdown({ onError }: { onError: (error: string | null) => void }) {
  const { t, language, languages, setLanguage } = useLanguage();
  const [expanded, setExpanded] = useState(false);
  return <View style={styles.container}>
    <Pressable accessibilityRole="button" accessibilityLabel={t("Language")}
      accessibilityState={{ expanded }} onPress={() => setExpanded(value => !value)} style={styles.control}>
      <Text style={styles.name}>{languages.find(option => option.code === language)?.name}</Text>
      <ChevronDown color={colors.muted} size={20} style={expanded ? styles.openIcon : undefined} />
    </Pressable>
    {expanded ? <ScrollView style={styles.menu} nestedScrollEnabled keyboardShouldPersistTaps="handled">
      {languages.map(option => <Pressable key={option.code} accessibilityRole="button" accessibilityLabel={option.name}
        accessibilityState={{ selected: option.code === language }} style={styles.option}
        onPress={() => {
          setExpanded(false); onError(null);
          void setLanguage(option.code).catch(error => onError(error instanceof Error ? error.message : t("Unable to save your language.")));
        }}>
        <Text style={styles.name}>{option.name}</Text>
        {option.code === language ? <Check color={colors.primary} size={18} /> : null}
      </Pressable>)}
    </ScrollView> : null}
  </View>;
}

const styles = StyleSheet.create({
  container: { gap: spacing.xs },
  control: { minHeight: 48, flexDirection: "row", alignItems: "center", justifyContent: "space-between",
    gap: spacing.md, padding: spacing.md, borderWidth: 1, borderColor: colors.border, borderRadius: 8 },
  menu: { maxHeight: 288, borderWidth: 1, borderColor: colors.border, borderRadius: 8, backgroundColor: colors.surfaceRaised },
  option: { minHeight: 48, padding: spacing.md, flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: spacing.md },
  name: { color: colors.text, fontSize: typography.body, flexShrink: 1 },
  openIcon: { transform: [{ rotate: "180deg" }] }
});
