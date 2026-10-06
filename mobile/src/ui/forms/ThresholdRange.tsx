import { useRef, useState } from "react";
import { View } from "react-native";
import { useLanguage } from "../../application/LanguageContext";
import { useTheme } from "../theme/ThemeProvider";
export function ThresholdRange({ off, on, onChange, disabled }: { off: number; on: number; onChange: (which: "off" | "on", value: number) => void; disabled?: boolean }) {
  const { colors } = useTheme(); const { t } = useLanguage(); const [width, setWidth] = useState(300); const active = useRef<"off" | "on">("on");
  const clamp = (value: number) => Number.isFinite(value) ? Math.max(0, Math.min(100, value)) : 0;
  const change = (x: number) => onChange(active.current, Math.round(clamp((x - 12) / Math.max(1, width - 24) * 100)));
  return <View onLayout={event => setWidth(event.nativeEvent.layout.width)} style={{ height: 64, justifyContent: "center", opacity: disabled ? .5 : 1 }} onStartShouldSetResponder={() => !disabled} onMoveShouldSetResponder={() => !disabled} onResponderGrant={event => { const percent = clamp((event.nativeEvent.locationX - 12) / Math.max(1, width - 24) * 100); active.current = Math.abs(percent - off) < Math.abs(percent - on) ? "off" : "on"; change(event.nativeEvent.locationX); }} onResponderMove={event => change(event.nativeEvent.locationX)}>
    <View style={{ height: 6, marginHorizontal: 12, borderRadius: 3, backgroundColor: colors.fill }}><View style={{ position: "absolute", left: `${clamp(off)}%`, right: `${100 - clamp(on)}%`, height: 6, backgroundColor: colors.sun, borderRadius: 3 }} /></View>
    {(["off", "on"] as const).map(which => { const value = clamp(which === "off" ? off : on); return <View key={which} accessibilityRole="adjustable" accessibilityLabel={t(which === "off" ? "Turn off below" : "Turn on at")} accessibilityValue={{ min: 0, max: 100, now: value, text: `${value}%` }} accessibilityState={{ disabled: Boolean(disabled) }} accessibilityActions={[{ name: "increment" }, { name: "decrement" }]} onAccessibilityAction={event => { if (!disabled) onChange(which, clamp(value + (event.nativeEvent.actionName === "increment" ? 1 : -1))); }} style={{ position: "absolute", left: 12 + value / 100 * Math.max(1, width - 24) - 12, width: 24, height: 24, borderRadius: 12, backgroundColor: colors.surface, borderWidth: 2, borderColor: colors.ink }} />; })}
  </View>;
}
