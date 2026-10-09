import { useLanguage } from "../application/LanguageContext";
import { createContext, type ReactNode, useContext, useMemo, useRef, useState } from "react";
import { ActivityIndicator, type KeyboardTypeOptions, type ReturnKeyTypeOptions, Pressable, RefreshControl, ScrollView, type StyleProp, StyleSheet, Switch, Text as NativeText, TextInput, type TextProps, View, type ViewStyle } from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";
import { HeaderHeightContext } from "@react-navigation/elements";
import { ChevronRight, Circle, type LucideIcon } from "lucide-react-native";
import { ScreenRefreshContext, type ScreenRefreshEntry, type ScreenRefreshOperation } from "./ScreenRefreshContext";
import { RefreshGroup } from "../features/energy/RefreshGroup";
import { useLegacyTheme, useTheme } from "../ui/theme/ThemeProvider";
import { uiFont } from "../ui/theme/fonts";
import { designTokens } from "../ui/theme/tokens";

export function ThemedText({ style, ...props }: TextProps) {
  const { colors } = useTheme(); const { language } = useLanguage();
  const flat = StyleSheet.flatten(style) ?? {};
  return <NativeText {...props} style={[{ color: colors.ink, fontSize: 17, lineHeight: 22, fontFamily: uiFont(flat.fontWeight ?? "500", language) }, style]} />;
}
const Text = ThemedText;
export const ScreenTopInsetContext = createContext(true);
function useStyles() { const { colors } = useLegacyTheme(); return useMemo(() => makeStyles(colors), [colors]); }

export function Screen({ children, scroll = true, refreshing, onRefresh, style }: {
  children: ReactNode; scroll?: boolean; refreshing?: boolean; onRefresh?: ScreenRefreshOperation; style?: StyleProp<ViewStyle>;
}) {
  const styles = useStyles(); const { colors } = useTheme(); const includeTop = useContext(ScreenTopInsetContext); const headerHeight = useContext(HeaderHeightContext) ?? 0;
  const [entries, setEntries] = useState(new Map<symbol, ScreenRefreshEntry>());
  const [pullPending, setPullPending] = useState(false); const pullActive = useRef(false); const group = useRef(new RefreshGroup()).current;
  const registry = useMemo(() => ({ register: (id: symbol, entry: ScreenRefreshEntry) => {
    setEntries(current => new Map(current).set(id, entry));
    return () => setEntries(current => { const next = new Map(current); next.delete(id); return next; });
  } }), []);
  const loading = Boolean(refreshing) || [...entries.values()].some(entry => entry.loading);
  const canRefresh = Boolean(onRefresh) || entries.size > 0;
  const refresh = async () => {
    if (loading || pullActive.current) return;
    pullActive.current = true; setPullPending(true);
    try { await group.run(false, [...(onRefresh ? [onRefresh] : []), ...[...entries.values()].map(entry => entry.refresh)]); }
    catch { /* Each resource retains safe data and exposes its own error. */ }
    finally { pullActive.current = false; setPullPending(false); }
  };
  const content = <View style={[styles.content, style]}>{children}</View>;
  return <ScreenRefreshContext.Provider value={registry}><SafeAreaView style={styles.safeArea} edges={includeTop && headerHeight === 0 ? ["top"] : []}>
    {scroll ? <ScrollView contentContainerStyle={styles.scrollContent} contentInsetAdjustmentBehavior="never" automaticallyAdjustContentInsets={false} automaticallyAdjustKeyboardInsets keyboardDismissMode="on-drag" keyboardShouldPersistTaps="handled" alwaysBounceVertical={canRefresh}
      refreshControl={canRefresh ? <RefreshControl refreshing={pullPending || loading} onRefresh={() => void refresh()} tintColor={colors.ink} colors={[colors.ink]} /> : undefined}>{content}</ScrollView> : content}
  </SafeAreaView></ScreenRefreshContext.Provider>;
}
export function Header({ title, subtitle, action }: { title: string; subtitle?: string; action?: ReactNode }) {
  const styles = useStyles(); const { t } = useLanguage();
  return <View style={styles.header}><View style={styles.headerCopy}><Text accessibilityRole="header" style={styles.title}>{t(title)}</Text>{subtitle ? <Text style={styles.subtitle}>{t(subtitle)}</Text> : null}</View>{action}</View>;
}
export function Card({ children, style }: { children: ReactNode; style?: StyleProp<ViewStyle> }) { const styles = useStyles(); return <View style={[styles.card, style]}>{children}</View>; }
export function SectionTitle({ title, trailing }: { title: string; trailing?: ReactNode }) { const styles = useStyles(); const { t } = useLanguage(); return <View style={styles.sectionTitle}><Text accessibilityRole="header" style={styles.sectionText}>{t(title)}</Text>{trailing}</View>; }

export function AppButton({ label, onPress, icon: Icon, variant = "primary", disabled, loading, compact, translateLabel = true, accessibilityLabel }: {
  label: string; onPress: () => void; icon?: LucideIcon; variant?: "primary" | "secondary" | "danger" | "critical" | "ghost" | "quiet" | "sun";
  disabled?: boolean; loading?: boolean; compact?: boolean; translateLabel?: boolean; accessibilityLabel?: string;
}) {
  const styles = useStyles(); const { colors } = useTheme(); const { t } = useLanguage();
  const palette = variant === "primary" ? { background: colors.ink, foreground: colors.bg, border: colors.ink }
    : variant === "sun" ? { background: colors.sun, foreground: colors.onSun, border: colors.sun }
    : variant === "danger" || variant === "critical" ? { background: "transparent", foreground: colors.criticalText, border: colors.criticalText }
    : variant === "quiet" ? { background: colors.fill, foreground: colors.ink, border: colors.fill }
    : { background: variant === "ghost" ? "transparent" : colors.surface, foreground: colors.ink, border: variant === "ghost" ? "transparent" : colors.line };
  return <Pressable accessibilityRole="button" accessibilityLabel={translateLabel ? t(accessibilityLabel ?? label) : accessibilityLabel ?? label}
    accessibilityState={{ disabled: Boolean(disabled || loading), busy: Boolean(loading) }} onPress={onPress} disabled={disabled || loading}
    style={({ pressed }) => [styles.button, compact && styles.buttonCompact, { backgroundColor: palette.background, borderColor: palette.border }, (pressed || disabled || loading) && styles.buttonPressed]}>
    {loading ? <ActivityIndicator color={palette.foreground} size="small" /> : Icon ? <Icon color={palette.foreground} size={20} strokeWidth={2} /> : null}
    <Text style={[styles.buttonText, { color: palette.foreground }]}>{translateLabel ? t(label) : label}</Text>
  </Pressable>;
}
export function IconButton({ icon: Icon, onPress, color, disabled, accessibilityLabel }: { icon: LucideIcon; onPress: () => void; color?: string; disabled?: boolean; accessibilityLabel: string }) {
  const styles = useStyles(); const { colors } = useTheme(); const { t } = useLanguage();
  return <Pressable accessibilityRole="button" accessibilityLabel={t(accessibilityLabel)} onPress={onPress} disabled={disabled} style={({ pressed }) => [styles.iconButton, (pressed || disabled) && styles.buttonPressed]}><Icon color={color ?? colors.ink} size={22} strokeWidth={2} /></Pressable>;
}
export function StatusPill({ label, tone = "neutral" }: { label: string; tone?: "success" | "warning" | "danger" | "info" | "neutral" }) {
  const styles = useStyles(); const { colors } = useTheme(); const { t } = useLanguage();
  const palette = tone === "success" ? [colors.batteryTint, colors.positiveText] : tone === "warning" ? [colors.warningTint, colors.warningText]
    : tone === "danger" ? [colors.criticalTint, colors.criticalText] : tone === "info" ? [colors.gridTint, colors.gridText] : [colors.fill, colors.ink2];
  return <View style={[styles.pill, { backgroundColor: palette[0] }]}><Circle size={6} color={palette[1]} fill={palette[1]} /><Text style={[styles.pillText, { color: palette[1] }]}>{t(label)}</Text></View>;
}
export function TextField({ label, value, onChangeText, placeholder, secureTextEntry, keyboardType = "default", multiline, editable = true, returnKeyType, onSubmitEditing, helper, error, unit, maxLength, autoComplete, textContentType, onBlur }: {
  label: string; value: string; onChangeText: (value: string) => void; placeholder?: string; secureTextEntry?: boolean; keyboardType?: KeyboardTypeOptions; multiline?: boolean; editable?: boolean; returnKeyType?: ReturnKeyTypeOptions; onSubmitEditing?: () => void;
  helper?: string; error?: string | null; unit?: string; maxLength?: number; autoComplete?: React.ComponentProps<typeof TextInput>["autoComplete"]; textContentType?: React.ComponentProps<typeof TextInput>["textContentType"]; onBlur?: () => void;
}) {
  const styles = useStyles(); const { colors } = useTheme(); const { language, t } = useLanguage();
  return <View style={[styles.field, !editable && styles.fieldDisabled]}><Text style={styles.fieldLabel}>{t(label)}</Text><View style={[styles.inputWrap, error ? { borderColor: colors.criticalText } : null]}>
    <TextInput value={value} onChangeText={onChangeText} placeholder={placeholder ? t(placeholder) : undefined} accessibilityLabel={t(label)} placeholderTextColor={colors.ink3} secureTextEntry={secureTextEntry} keyboardType={keyboardType} multiline={multiline} editable={editable} returnKeyType={returnKeyType} onSubmitEditing={onSubmitEditing} autoCapitalize="none" maxLength={maxLength} autoComplete={autoComplete} textContentType={textContentType} onBlur={onBlur}
      style={[styles.input, { fontFamily: uiFont("500", language) }, multiline && styles.multilineInput]} />{unit ? <Text style={styles.unit}>{unit}</Text> : null}
  </View>{error ? <Text accessibilityRole="alert" style={{ color: colors.criticalText, fontSize: 13 }}>{t(error)}</Text> : helper ? <Text style={styles.fieldHelper}>{t(helper)}</Text> : null}</View>;
}
export function NativeSwitch({ label, value, onValueChange, disabled }: { label: string; value: boolean; onValueChange: (value: boolean) => void; disabled?: boolean }) {
  const { colors } = useTheme(); const { t } = useLanguage();
  return <View style={{ minHeight: 44, justifyContent: "center" }}><Switch accessibilityLabel={t(label)} value={value} onValueChange={onValueChange} disabled={disabled} thumbColor={value ? colors.switchOnKnob : colors.switchKnob} trackColor={{ false: colors.switchOffTrack, true: colors.switchOnTrack }} ios_backgroundColor={colors.switchOffTrack} /></View>;
}
export function SwitchRow({ title, subtitle, value, onValueChange, disabled }: { title: string; subtitle?: string; value: boolean; onValueChange: (value: boolean) => void; disabled?: boolean }) {
  const styles = useStyles(); const { t } = useLanguage();
  return <View style={styles.switchRow}><View style={styles.switchCopy}><Text style={styles.switchTitle}>{t(title)}</Text>{subtitle ? <Text style={styles.switchSubtitle}>{t(subtitle)}</Text> : null}</View><NativeSwitch label={title} value={value} onValueChange={onValueChange} disabled={disabled} /></View>;
}
export function SegmentedControl<T extends string>({ options, value, onChange }: { options: { label: string; value: T }[]; value: T; onChange: (value: T) => void }) {
  const styles = useStyles(); const { t } = useLanguage();
  return <View style={styles.segmented}>{options.map(option => <Pressable key={option.value} hitSlop={4} accessibilityRole="button" accessibilityLabel={t(option.label)} accessibilityState={{ selected: option.value === value }} onPress={() => onChange(option.value)} style={[styles.segment, option.value === value && styles.segmentSelected]}><Text style={[styles.segmentText, option.value === value && styles.segmentTextSelected]}>{t(option.label)}</Text></Pressable>)}</View>;
}
export function ProgressBar({ value, color }: { value: number; color?: string }) { const styles = useStyles(); const { colors } = useTheme(); return <View style={styles.progressTrack}><View style={[styles.progressFill, { width: `${Math.max(0, Math.min(100, value))}%`, backgroundColor: color ?? colors.ink }]} /></View>; }
export function MetricTile({ label, value, detail, icon: Icon, color }: { label: string; value: string; detail?: string; icon: LucideIcon; color?: string }) { const styles = useStyles(); const { colors } = useTheme(); const { t } = useLanguage(); return <Card style={styles.metricTile}><View style={styles.metricTop}><Icon color={color ?? colors.ink} size={20} /><Text style={styles.metricLabel}>{t(label)}</Text></View><Text style={styles.metricValue}>{value}</Text>{detail ? <Text style={styles.metricDetail}>{t(detail)}</Text> : null}</Card>; }
export function LoadingState({ label = "Loading..." }: { label?: string }) { const styles = useStyles(); const { t } = useLanguage(); return <View accessibilityLabel={t(label)} accessibilityState={{ busy: true }} style={styles.skeletons}>{[72, 180, 110].map((height, i) => <View key={i} style={[styles.skeleton, { height }]} />)}</View>; }
export function EmptyState({ title, detail }: { title: string; detail?: string }) { const styles = useStyles(); const { t } = useLanguage(); return <View style={styles.centerStateCard}><Text style={styles.emptyTitle}>{t(title)}</Text>{detail ? <Text style={styles.emptyDetail}>{t(detail)}</Text> : null}</View>; }
export function ErrorBanner({ message }: { message?: string | null }) { const styles = useStyles(); const { t } = useLanguage(); return message ? <View style={styles.errorBanner}><Text accessibilityRole="alert" style={styles.errorText}>{t(message)}</Text></View> : null; }
export function Banner({ children, tone = "neutral" }: { children: ReactNode; tone?: "neutral" | "warning" | "sample" }) { const { colors } = useTheme(); return <View style={{ padding: 16, borderRadius: 16, backgroundColor: tone === "sample" ? colors.sunTint : tone === "warning" ? colors.warningTint : colors.fill }}>{typeof children === "string" ? <Text style={{ fontSize: 13, lineHeight: 18 }}>{children}</Text> : children}</View>; }
export function Group({ children }: { children: ReactNode }) { const { colors } = useTheme(); return <View style={{ backgroundColor: colors.surface, borderRadius: 20, overflow: "hidden" }}>{children}</View>; }
export function NavigationRow({ title, value, subtitle, onPress, icon: Icon, critical }: { title: string; value?: string; subtitle?: string; onPress: () => void; icon?: LucideIcon; critical?: boolean }) {
  const { colors } = useTheme(); const { t } = useLanguage();
  return <Pressable accessibilityRole="button" accessibilityLabel={t(title)} onPress={onPress} style={({ pressed }) => ({ minHeight: 56, paddingHorizontal: 16, paddingVertical: 12, flexDirection: "row", alignItems: "center", gap: 12, opacity: pressed ? .6 : 1 })}>
    {Icon ? <Icon size={20} color={critical ? colors.criticalText : colors.ink2} /> : null}<View style={{ flex: 1 }}><Text style={{ color: critical ? colors.criticalText : colors.ink }}>{t(title)}</Text>{subtitle ? <Text style={{ color: colors.ink3, fontSize: 13 }}>{t(subtitle)}</Text> : null}</View>{value ? <Text style={{ color: colors.ink3, fontSize: 15 }}>{value}</Text> : null}<ChevronRight size={18} color={colors.ink3} />
  </Pressable>;
}
export function DataRow({ label, value, detail }: { label: string; value: string; detail?: string }) { const { colors } = useTheme(); const { t } = useLanguage(); return <View style={{ minHeight: 52, flexDirection: "row", alignItems: "center", gap: 16, paddingVertical: 10 }}><View style={{ flex: 1 }}><Text style={{ color: colors.ink2 }}>{t(label)}</Text>{detail ? <Text style={{ color: colors.ink3, fontSize: 13 }}>{t(detail)}</Text> : null}</View><Text style={{ fontVariant: ["tabular-nums"], fontWeight: "600" }}>{value}</Text></View>; }

const makeStyles = (colors: ReturnType<typeof useLegacyTheme>["colors"]) => StyleSheet.create({
  safeArea: { flex: 1, backgroundColor: colors.background }, scrollContent: { paddingBottom: 112 }, content: { paddingHorizontal: 16, paddingTop: 16, gap: 16 },
  header: { minHeight: 48, flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: 12 }, headerCopy: { flex: 1 }, title: { color: colors.text, fontSize: 34, lineHeight: 40, fontWeight: "700", letterSpacing: -.68 }, subtitle: { marginTop: 4, color: colors.muted, fontSize: 15, lineHeight: 20 },
  card: { backgroundColor: colors.surface, borderRadius: 24, padding: 20 }, sectionTitle: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: 12 }, sectionText: { color: colors.text, fontSize: 19, lineHeight: 24, fontWeight: "700" },
  button: { minHeight: 52, borderWidth: 1, borderRadius: 12, paddingHorizontal: 16, paddingVertical: 10, alignItems: "center", justifyContent: "center", flexDirection: "row", gap: 8 }, buttonCompact: { minHeight: 44, paddingHorizontal: 12 }, buttonPressed: { opacity: .5 }, buttonText: { minWidth: 0, flexShrink: 1, textAlign: "center", fontSize: 17, lineHeight: 22, fontWeight: "600" },
  iconButton: { width: 44, height: 44, borderRadius: 999, alignItems: "center", justifyContent: "center", backgroundColor: colors.glassButton, ...designTokens.shadows.glassButton },
  pill: { alignSelf: "flex-start", flexDirection: "row", alignItems: "center", gap: 5, borderRadius: 999, paddingHorizontal: 9, paddingVertical: 4 }, pillText: { fontSize: 13, lineHeight: 18, fontWeight: "600" },
  field: { gap: 6 }, fieldDisabled: { opacity: .5 }, fieldLabel: { color: colors.muted, fontSize: 13, lineHeight: 18, fontWeight: "600" }, inputWrap: { flexDirection: "row", alignItems: "center", borderWidth: 1, borderColor: colors.line, borderRadius: 12, backgroundColor: colors.surface }, input: { minHeight: 52, flex: 1, color: colors.text, paddingHorizontal: 14, fontSize: 17 }, unit: { color: colors.muted, paddingRight: 14, fontSize: 15 }, fieldHelper: { color: colors.subtle, fontSize: 13, lineHeight: 18 }, multilineInput: { minHeight: 88, textAlignVertical: "top", paddingTop: 12 },
  switchRow: { minHeight: 58, flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: 16 }, switchCopy: { flex: 1 }, switchTitle: { color: colors.text, fontSize: 17 }, switchSubtitle: { marginTop: 4, color: colors.muted, fontSize: 13, lineHeight: 18 },
  segmented: { flexDirection: "row", padding: 4, gap: 4, backgroundColor: colors.fill, borderRadius: 12 }, segment: { flex: 1, minHeight: 36, alignItems: "center", justifyContent: "center", borderRadius: 9, paddingHorizontal: 6 }, segmentSelected: { backgroundColor: colors.surface, ...designTokens.shadows.segmentSelected }, segmentText: { color: colors.muted, fontSize: 15, lineHeight: 20, fontWeight: "600" }, segmentTextSelected: { color: colors.text },
  progressTrack: { height: 8, borderRadius: 4, backgroundColor: colors.fill, overflow: "hidden" }, progressFill: { height: 8, borderRadius: 4 }, metricTile: { flex: 1, minWidth: 140, gap: 8, padding: 16 }, metricTop: { flexDirection: "row", alignItems: "center", gap: 8 }, metricLabel: { color: colors.muted, fontSize: 13, lineHeight: 18 }, metricValue: { color: colors.text, fontSize: 30, lineHeight: 36, fontWeight: "700" }, metricDetail: { color: colors.muted, fontSize: 13, lineHeight: 18 },
  skeletons: { gap: 16 }, skeleton: { backgroundColor: colors.fill, borderRadius: 24 }, centerStateCard: { gap: 8, padding: 20 }, emptyTitle: { color: colors.text, fontSize: 19, lineHeight: 24, fontWeight: "700" }, emptyDetail: { color: colors.muted, fontSize: 15, lineHeight: 20 }, errorBanner: { backgroundColor: colors.criticalTint, borderRadius: 16, padding: 16 }, errorText: { color: colors.criticalText, fontSize: 15, lineHeight: 20 }
});
