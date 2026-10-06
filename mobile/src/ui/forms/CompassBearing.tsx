import type { ReactNode } from "react";
import { Pressable, View } from "react-native";
import Svg, { Circle, G, Line, Path } from "react-native-svg";
import { useLanguage } from "../../application/LanguageContext";
import { ThemedText as Text } from "../../core/components";
import { useTheme } from "../theme/ThemeProvider";
import { compassPoint, compassPresets } from "./compassPolicy";

export function CompassBearing({ value, onChange, disabled, children }: { value: number; onChange(value: number): void; disabled: boolean; children: ReactNode }) {
  const { colors } = useTheme(); const { t } = useLanguage(); const point = compassPoint(value);
  return <View style={{ flexDirection: "row", alignItems: "center", gap: 12 }}>
    <View style={{ width: 144, height: 144 }}>
      <Svg width={144} height={144} viewBox="0 0 144 144" accessible={false}>
        <Circle cx={72} cy={72} r={49} fill={colors.fill} stroke={colors.lineStrong} strokeWidth={1.5} />
        {Array.from({ length: 12 }, (_, index) => <G key={index} rotation={index * 30} origin="72,72"><Line x1={72} y1={22} x2={72} y2={27} stroke={colors.lineStrong} strokeWidth={1.5} /></G>)}
        {point ? <><Line testID="compass-needle" x1={72} y1={72} x2={point.x} y2={point.y} stroke={colors.ink} strokeWidth={3} strokeLinecap="round" /><G rotation={value} origin="72,72"><Path d="M72 37 L79 51 L65 51 Z" fill={colors.sun} stroke={colors.ink} strokeWidth={1.5} strokeLinejoin="round" /></G><Circle cx={72} cy={72} r={4} fill={colors.ink} /></> : null}
      </Svg>
      {compassPresets.map((preset, index) => { const selected = value === preset.value || value === 360 && preset.value === 0; return <Pressable key={preset.label} accessibilityRole="button" accessibilityLabel={`${t("Compass bearing · degrees")} ${preset.value}° ${preset.label}`} accessibilityState={{ selected, disabled }} disabled={disabled} onPress={() => { if (!disabled) onChange(preset.value); }} style={{ position: "absolute", minWidth: 44, minHeight: 44, alignItems: "center", justifyContent: "center", borderRadius: 22, top: index === 0 ? 0 : index === 2 ? 100 : 50, left: index === 3 ? 0 : index === 1 ? 100 : 50, backgroundColor: selected ? colors.sunTint : "transparent", opacity: disabled ? 0.55 : 1 }}><Text style={{ fontSize: 13, fontWeight: "600", color: selected ? colors.ink : colors.ink2 }}>{preset.label}</Text></Pressable>; })}
    </View><View style={{ flex: 1 }}>{children}</View>
  </View>;
}
