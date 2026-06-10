import { ReactNode } from "react";
import {
  ActivityIndicator,
  KeyboardTypeOptions,
  Pressable,
  RefreshControl,
  ScrollView,
  StyleProp,
  StyleSheet,
  Switch,
  Text,
  TextInput,
  View,
  ViewStyle
} from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";
import { LucideIcon } from "lucide-react-native";
import { colors, radius, spacing, typography } from "./theme";

export function Screen({
  children,
  scroll = true,
  refreshing,
  onRefresh,
  style
}: {
  children: ReactNode;
  scroll?: boolean;
  refreshing?: boolean;
  onRefresh?: () => void;
  style?: StyleProp<ViewStyle>;
}) {
  const content = <View style={[styles.content, style]}>{children}</View>;

  return (
    <SafeAreaView style={styles.safeArea} edges={["top"]}>
      {scroll ? (
        <ScrollView
          contentContainerStyle={styles.scrollContent}
          keyboardShouldPersistTaps="handled"
          refreshControl={
            onRefresh
              ? (
                  <RefreshControl
                    refreshing={Boolean(refreshing)}
                    onRefresh={onRefresh}
                    tintColor={colors.primary}
                  />
                )
              : undefined
          }
        >
          {content}
        </ScrollView>
      ) : (
        content
      )}
    </SafeAreaView>
  );
}

export function Header({
  title,
  subtitle,
  action
}: {
  title: string;
  subtitle?: string;
  action?: ReactNode;
}) {
  return (
    <View style={styles.header}>
      <View style={styles.headerCopy}>
        <Text style={styles.title}>{title}</Text>
        {subtitle ? <Text style={styles.subtitle}>{subtitle}</Text> : null}
      </View>
      {action}
    </View>
  );
}

export function Card({ children, style }: { children: ReactNode; style?: StyleProp<ViewStyle> }) {
  return <View style={[styles.card, style]}>{children}</View>;
}

export function SectionTitle({ title, trailing }: { title: string; trailing?: ReactNode }) {
  return (
    <View style={styles.sectionTitle}>
      <Text style={styles.sectionText}>{title}</Text>
      {trailing}
    </View>
  );
}

export function AppButton({
  label,
  onPress,
  icon: Icon,
  variant = "primary",
  disabled,
  loading,
  compact
}: {
  label: string;
  onPress: () => void;
  icon?: LucideIcon;
  variant?: "primary" | "secondary" | "danger" | "ghost";
  disabled?: boolean;
  loading?: boolean;
  compact?: boolean;
}) {
  const palette = buttonPalette[variant];
  return (
    <Pressable
      onPress={onPress}
      disabled={disabled || loading}
      style={({ pressed }) => [
        styles.button,
        compact && styles.buttonCompact,
        { backgroundColor: palette.background, borderColor: palette.border },
        (pressed || disabled || loading) && styles.buttonPressed
      ]}
    >
      {loading ? (
        <ActivityIndicator color={palette.foreground} size="small" />
      ) : Icon ? (
        <Icon color={palette.foreground} size={18} strokeWidth={2.2} />
      ) : null}
      <Text style={[styles.buttonText, { color: palette.foreground }]}>{label}</Text>
    </Pressable>
  );
}

export function IconButton({
  icon: Icon,
  onPress,
  color = colors.text,
  disabled
}: {
  icon: LucideIcon;
  onPress: () => void;
  color?: string;
  disabled?: boolean;
}) {
  return (
    <Pressable
      onPress={onPress}
      disabled={disabled}
      style={({ pressed }) => [styles.iconButton, (pressed || disabled) && styles.buttonPressed]}
    >
      <Icon color={color} size={20} strokeWidth={2.2} />
    </Pressable>
  );
}

export function StatusPill({
  label,
  tone = "neutral"
}: {
  label: string;
  tone?: "success" | "warning" | "danger" | "info" | "neutral";
}) {
  const palette = pillPalette[tone];
  return (
    <View style={[styles.pill, { backgroundColor: palette.background, borderColor: palette.border }]}>
      <Text style={[styles.pillText, { color: palette.foreground }]}>{label}</Text>
    </View>
  );
}

export function TextField({
  label,
  value,
  onChangeText,
  placeholder,
  secureTextEntry,
  keyboardType = "default",
  multiline
}: {
  label: string;
  value: string;
  onChangeText: (value: string) => void;
  placeholder?: string;
  secureTextEntry?: boolean;
  keyboardType?: KeyboardTypeOptions;
  multiline?: boolean;
}) {
  return (
    <View style={styles.field}>
      <Text style={styles.fieldLabel}>{label}</Text>
      <TextInput
        value={value}
        onChangeText={onChangeText}
        placeholder={placeholder}
        placeholderTextColor={colors.subtle}
        secureTextEntry={secureTextEntry}
        keyboardType={keyboardType}
        multiline={multiline}
        autoCapitalize="none"
        style={[styles.input, multiline && styles.multilineInput]}
      />
    </View>
  );
}

export function SwitchRow({
  title,
  subtitle,
  value,
  onValueChange,
  disabled
}: {
  title: string;
  subtitle?: string;
  value: boolean;
  onValueChange: (value: boolean) => void;
  disabled?: boolean;
}) {
  return (
    <View style={styles.switchRow}>
      <View style={styles.switchCopy}>
        <Text style={styles.switchTitle}>{title}</Text>
        {subtitle ? <Text style={styles.switchSubtitle}>{subtitle}</Text> : null}
      </View>
      <Switch
        value={value}
        onValueChange={onValueChange}
        disabled={disabled}
        thumbColor={value ? colors.primary : colors.muted}
        trackColor={{ false: colors.off, true: colors.primaryDark }}
      />
    </View>
  );
}

export function SegmentedControl<T extends string>({
  options,
  value,
  onChange
}: {
  options: { label: string; value: T }[];
  value: T;
  onChange: (value: T) => void;
}) {
  return (
    <View style={styles.segmented}>
      {options.map((option) => {
        const selected = option.value === value;
        return (
          <Pressable
            key={option.value}
            onPress={() => onChange(option.value)}
            style={[styles.segment, selected && styles.segmentSelected]}
          >
            <Text style={[styles.segmentText, selected && styles.segmentTextSelected]}>
              {option.label}
            </Text>
          </Pressable>
        );
      })}
    </View>
  );
}

export function ProgressBar({
  value,
  color = colors.primary
}: {
  value: number;
  color?: string;
}) {
  const safeValue = Math.max(0, Math.min(100, value));
  return (
    <View style={styles.progressTrack}>
      <View style={[styles.progressFill, { width: `${safeValue}%`, backgroundColor: color }]} />
    </View>
  );
}

export function MetricTile({
  label,
  value,
  detail,
  icon: Icon,
  color = colors.primary
}: {
  label: string;
  value: string;
  detail?: string;
  icon: LucideIcon;
  color?: string;
}) {
  return (
    <Card style={styles.metricTile}>
      <View style={styles.metricTop}>
        <Icon color={color} size={20} strokeWidth={2.2} />
        <Text style={styles.metricLabel}>{label}</Text>
      </View>
      <Text style={styles.metricValue}>{value}</Text>
      {detail ? <Text style={styles.metricDetail}>{detail}</Text> : null}
    </Card>
  );
}

export function LoadingState({ label = "Loading..." }: { label?: string }) {
  return (
    <View style={styles.centerState}>
      <ActivityIndicator color={colors.primary} />
      <Text style={styles.centerStateText}>{label}</Text>
    </View>
  );
}

export function EmptyState({ title, detail }: { title: string; detail?: string }) {
  return (
    <View style={styles.centerStateCard}>
      <Text style={styles.emptyTitle}>{title}</Text>
      {detail ? <Text style={styles.emptyDetail}>{detail}</Text> : null}
    </View>
  );
}

export function ErrorBanner({ message }: { message?: string | null }) {
  if (!message) {
    return null;
  }

  return (
    <View style={styles.errorBanner}>
      <Text style={styles.errorText}>{message}</Text>
    </View>
  );
}

const buttonPalette = {
  primary: { background: colors.primary, foreground: "#07120c", border: colors.primary },
  secondary: { background: colors.surfaceRaised, foreground: colors.text, border: colors.border },
  danger: { background: colors.red, foreground: "#1f0505", border: colors.red },
  ghost: { background: "transparent", foreground: colors.text, border: colors.border }
};

const pillPalette = {
  success: { background: "#143b28", foreground: colors.primary, border: "#1f6f45" },
  warning: { background: "#3d2e12", foreground: colors.amber, border: "#735221" },
  danger: { background: "#421e22", foreground: colors.red, border: "#7f333a" },
  info: { background: "#172f45", foreground: colors.blue, border: "#245477" },
  neutral: { background: colors.surfaceRaised, foreground: colors.muted, border: colors.border }
};

const styles = StyleSheet.create({
  safeArea: {
    flex: 1,
    backgroundColor: colors.background
  },
  scrollContent: {
    paddingBottom: 112
  },
  content: {
    paddingHorizontal: spacing.lg,
    paddingTop: spacing.lg,
    gap: spacing.lg
  },
  header: {
    minHeight: 48,
    flexDirection: "row",
    alignItems: "flex-start",
    justifyContent: "space-between",
    gap: spacing.md
  },
  headerCopy: {
    flex: 1
  },
  title: {
    color: colors.text,
    fontSize: typography.title,
    fontWeight: "700"
  },
  subtitle: {
    marginTop: spacing.xs,
    color: colors.muted,
    fontSize: typography.body
  },
  card: {
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: colors.border,
    backgroundColor: colors.surface,
    borderRadius: radius.md,
    padding: spacing.lg
  },
  sectionTitle: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: spacing.md
  },
  sectionText: {
    color: colors.text,
    fontSize: typography.section,
    fontWeight: "700"
  },
  button: {
    minHeight: 44,
    borderWidth: StyleSheet.hairlineWidth,
    borderRadius: radius.md,
    paddingHorizontal: spacing.lg,
    alignItems: "center",
    justifyContent: "center",
    flexDirection: "row",
    gap: spacing.sm
  },
  buttonCompact: {
    minHeight: 36,
    paddingHorizontal: spacing.md
  },
  buttonPressed: {
    opacity: 0.65
  },
  buttonText: {
    fontSize: 14,
    fontWeight: "700"
  },
  iconButton: {
    width: 40,
    height: 40,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: colors.border,
    borderRadius: radius.md,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: colors.surfaceRaised
  },
  pill: {
    alignSelf: "flex-start",
    borderWidth: StyleSheet.hairlineWidth,
    borderRadius: radius.sm,
    paddingHorizontal: spacing.sm,
    paddingVertical: spacing.xs
  },
  pillText: {
    fontSize: 12,
    fontWeight: "700"
  },
  field: {
    gap: spacing.xs
  },
  fieldLabel: {
    color: colors.muted,
    fontSize: typography.caption,
    fontWeight: "700",
    textTransform: "uppercase"
  },
  input: {
    minHeight: 46,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: colors.border,
    borderRadius: radius.md,
    backgroundColor: colors.surfaceRaised,
    color: colors.text,
    paddingHorizontal: spacing.md,
    fontSize: typography.body
  },
  multilineInput: {
    minHeight: 88,
    textAlignVertical: "top",
    paddingTop: spacing.md
  },
  switchRow: {
    minHeight: 58,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: spacing.lg
  },
  switchCopy: {
    flex: 1
  },
  switchTitle: {
    color: colors.text,
    fontSize: typography.body,
    fontWeight: "700"
  },
  switchSubtitle: {
    marginTop: spacing.xs,
    color: colors.muted,
    fontSize: typography.caption
  },
  segmented: {
    flexDirection: "row",
    padding: spacing.xs,
    gap: spacing.xs,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: colors.border,
    backgroundColor: colors.surface,
    borderRadius: radius.md
  },
  segment: {
    flex: 1,
    minHeight: 34,
    alignItems: "center",
    justifyContent: "center",
    borderRadius: radius.sm,
    paddingHorizontal: spacing.sm
  },
  segmentSelected: {
    backgroundColor: colors.surfaceRaised
  },
  segmentText: {
    color: colors.muted,
    fontSize: 13,
    fontWeight: "700"
  },
  segmentTextSelected: {
    color: colors.primary
  },
  progressTrack: {
    height: 8,
    borderRadius: 4,
    backgroundColor: colors.surfaceRaised,
    overflow: "hidden"
  },
  progressFill: {
    height: 8,
    borderRadius: 4
  },
  metricTile: {
    flex: 1,
    minWidth: 148,
    gap: spacing.sm
  },
  metricTop: {
    flexDirection: "row",
    alignItems: "center",
    gap: spacing.sm
  },
  metricLabel: {
    color: colors.muted,
    fontSize: typography.caption,
    fontWeight: "700",
    textTransform: "uppercase"
  },
  metricValue: {
    color: colors.text,
    fontSize: typography.metric,
    fontWeight: "800"
  },
  metricDetail: {
    color: colors.muted,
    fontSize: typography.caption
  },
  centerState: {
    minHeight: 220,
    alignItems: "center",
    justifyContent: "center",
    gap: spacing.md
  },
  centerStateText: {
    color: colors.muted,
    fontSize: typography.body
  },
  centerStateCard: {
    alignItems: "center",
    gap: spacing.sm,
    padding: spacing.lg
  },
  emptyTitle: {
    color: colors.text,
    fontSize: typography.body,
    fontWeight: "700",
    textAlign: "center"
  },
  emptyDetail: {
    color: colors.muted,
    fontSize: typography.caption,
    textAlign: "center"
  },
  errorBanner: {
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: "#7f333a",
    backgroundColor: "#421e22",
    borderRadius: radius.md,
    padding: spacing.md
  },
  errorText: {
    color: colors.red,
    fontSize: typography.body
  }
});
