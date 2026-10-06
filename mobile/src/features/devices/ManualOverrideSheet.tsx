import { Modal, Pressable, View } from "react-native";
import { useLanguage } from "../../application/LanguageContext";
import { useTheme } from "../../ui/theme/ThemeProvider";
import { AppButton, ThemedText as Text } from "../../core/components";
import type { Device, Rule } from "../../core/api/types";
import type { RuleConflictChoice } from "../../core/api/IntegrationApi";

export function ManualOverrideSheet({ device, rules, isOn, busy, canControl, canPause, onChoose, onCancel }: {
  device: Device; rules: Rule[]; isOn: boolean; busy: boolean; canControl: boolean; canPause: boolean;
  onChoose: (choice: RuleConflictChoice) => void; onCancel: () => void;
}) {
  const { colors } = useTheme(); const { t } = useLanguage();
  const interval = Math.min(...rules.map(rule => rule.intervalSeconds));
  return <Modal transparent animationType="slide" onRequestClose={busy ? () => {} : onCancel}>
    <View style={{ flex: 1, justifyContent: "flex-end", backgroundColor: colors.scrim }}>
      <Pressable accessibilityLabel={t("Cancel")} onPress={onCancel} disabled={busy} style={{ flex: 1 }} />
      <View accessibilityViewIsModal style={{ backgroundColor: colors.surface, borderRadius: 38, margin: 8, padding: 24, paddingBottom: 36, gap: 16 }}>
        <View style={{ width: 36, height: 5, borderRadius: 3, backgroundColor: colors.line, alignSelf: "center" }} />
        <Text accessibilityRole="header" style={{ fontSize: 22, lineHeight: 28, fontWeight: "700" }}>{t("Switch {0} {1}?", device.name, t(isOn ? "on" : "off"))}</Text>
        <Text style={{ color: colors.ink2, fontSize: 15 }}>{t("An automation controls this device. Choose what happens after your switch.")}</Text>
        <AppButton label={rules.length === 1 ? t("Pause {0}", rules[0]!.name) : t("Pause {0} automations", rules.length)} translateLabel={false} loading={busy} disabled={busy || !canControl || !canPause} onPress={() => { if (!busy && canControl && canPause) onChoose("pause"); }} />
        <Text style={{ color: colors.ink2, fontSize: 13 }}>{t("Switch the device and pause its enabled automations until you resume them.")}</Text>
        <AppButton label="Just this once" variant="secondary" disabled={busy || !canControl} onPress={() => { if (!busy && canControl) onChoose("once"); }} />
        <Text style={{ color: colors.ink2, fontSize: 13 }}>{t("An automation may switch it back within {0} seconds.", interval)}</Text>
        <AppButton label="Cancel" variant="ghost" disabled={busy} onPress={onCancel} />
      </View>
    </View>
  </Modal>;
}
