import { useState } from "react";
import { Text, View } from "react-native";
import { AppButton } from "../../core/components";
import { colors, spacing } from "../../core/theme";

export function IntegrationSelect({ label, value, options, disabled, onChange }: {
  label: string; value: string; options: { value: string; label: string; disabled?: boolean }[];
  disabled?: boolean; onChange: (value: string) => void;
}) {
  const [expanded, setExpanded] = useState(false);
  return <View style={{ gap: spacing.sm }}>
    <Text style={{ color: colors.muted }}>{label}</Text>
    <AppButton label={`${options.find(option => option.value === value)?.label ?? "Select manufacturer"} ▾`}
      accessibilityLabel={label} variant="secondary" disabled={disabled}
      onPress={() => setExpanded(current => !current)} />
    {expanded && !disabled ? <View style={{ gap: spacing.sm }}>
      {options.map(option => <AppButton key={option.value} label={option.label}
        variant={value === option.value ? "primary" : "secondary"} disabled={option.disabled}
        onPress={() => { onChange(option.value); setExpanded(false); }} />)}
      {!options.length ? <Text style={{ color: colors.muted }}>No providers installed.</Text> : null}
    </View> : null}
  </View>;
}
