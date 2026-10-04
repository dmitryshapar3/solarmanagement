import { formattingLocale } from "../../core/i18n";
import { useLanguage } from "../../application/LanguageContext";
import { useMemo, useState } from "react";
import { Pressable, StyleSheet, Text, View } from "react-native";
import Svg, { Circle, Line, Path, Rect, Text as SvgText } from "react-native-svg";
import { colors, spacing } from "../../core/theme";
import { ChartPoint, chartGeometry, chartHeight, chartWidth, defaultPointIndex } from "./chartPolicy";

export function EnergyChart({ points, mode, unit }: { points: ChartPoint[]; mode: "generation" | "sales"; unit: string }) {
  const { t } = useLanguage();
  const [selectedTime, setSelectedTime] = useState<string | null>(null);
  const geometry = useMemo(() => chartGeometry(points, mode), [points, mode]);
  const selected = Math.max(0, selectedTime && points.some((point) => point.timestamp === selectedTime)
    ? points.findIndex((point) => point.timestamp === selectedTime) : defaultPointIndex(points));
  const point = points[selected];
  const hasValues = mode === "generation" ? geometry.bandPaths.length + geometry.actualPaths.length > 0 : geometry.bars.length > 0;
  const ticks = Array.from(new Set(Array.from({ length: Math.min(5, points.length) }, (_, index) => Math.round(index * (points.length - 1) / Math.max(1, Math.min(5, points.length) - 1)))));
  const accent = mode === "sales" && unit === "PLN" ? colors.amber : colors.primary;
  return (
    <View style={styles.container}>
      {hasValues ? <Svg width="100%" height={chartHeight} viewBox={`0 0 ${chartWidth} ${chartHeight}`} accessibilityLabel={t("{0} chart in {1}", mode === "generation" ? t("Possible range and actual generation") : t("Completed and provisional sales"), unit)}>
        {geometry.ticks.map((tick, index) => <Line key={`line-${index}`} x1={38} x2={348} y1={tick.y} y2={tick.y} stroke={colors.border} strokeWidth={.6} />)}
        {geometry.ticks.map((tick, index) => <SvgText key={`label-${index}`} x={32} y={tick.y + 4} fill={colors.muted} fontSize={9} textAnchor="end">{tick.value.toLocaleString(formattingLocale(), { maximumFractionDigits: 2 })}</SvgText>)}
        <SvgText x={38} y={10} fill={colors.muted} fontSize={9}>{unit}</SvgText>
        <Line x1={38} x2={348} y1={geometry.baseline} y2={geometry.baseline} stroke={colors.muted} strokeWidth={.7} />
        {mode === "generation" ? <>
          {geometry.bandPaths.map((path, index) => <Path key={`band-${index}`} d={path} fill={colors.amber} fillOpacity={.22} stroke={colors.amber} strokeOpacity={.55} strokeWidth={.6} />)}
          {geometry.actualPaths.map((path, index) => <Path key={`actual-${index}`} d={path} fill="none" stroke={colors.primary} strokeWidth={2} />)}
          {geometry.actualDots.map((dot, index) => <Circle key={`dot-${index}`} cx={dot.x} cy={dot.y} r={1.7} fill={colors.primary} />)}
        </> : geometry.bars.map((bar, index) => <Rect key={`bar-${index}`} x={bar.x} y={bar.y} width={bar.width} height={bar.height} fill={accent} fillOpacity={bar.provisional ? .2 : .85}
          stroke={bar.provisional ? accent : "none"} strokeDasharray={bar.provisional ? "3 2" : undefined} strokeWidth={1} />)}
        {point ? <Line x1={geometry.x(selected)} x2={geometry.x(selected)} y1={18} y2={176} stroke={colors.muted} strokeDasharray="3 4" /> : null}
        {points.map((item, index) => <Rect key={item.timestamp} x={geometry.x(index) - geometry.step / 2} y={16} width={geometry.step} height={164} fill="transparent" onPress={() => setSelectedTime(item.timestamp)} />)}
        {ticks.map((index) => <SvgText key={`time-${index}`} x={geometry.x(index)} y={198} fill={colors.muted} fontSize={9} textAnchor="middle">{points[index]?.label}</SvgText>)}
      </Svg> : <Text style={styles.empty}>{t("No values available for this period yet. Missing data is not zero.")}</Text>}
      {point ? <View style={styles.inspector}>
        <View style={styles.navigation}>
          <Pressable accessibilityRole="button" accessibilityLabel={t("Previous interval")} disabled={selected <= 0} onPress={() => setSelectedTime(points[selected - 1]!.timestamp)} style={[styles.arrow, selected <= 0 && styles.disabled]}><Text style={styles.arrowText}>‹</Text></Pressable>
          <Text style={styles.description} accessibilityLiveRegion="polite">{point.description}</Text>
          <Pressable accessibilityRole="button" accessibilityLabel={t("Next interval")} disabled={selected >= points.length - 1} onPress={() => setSelectedTime(points[selected + 1]!.timestamp)} style={[styles.arrow, selected >= points.length - 1 && styles.disabled]}><Text style={styles.arrowText}>›</Text></Pressable>
        </View>
      </View> : null}
    </View>
  );
}

const styles = StyleSheet.create({
  container: { gap: spacing.sm }, empty: { color: colors.muted, paddingVertical: 36, textAlign: "center", lineHeight: 19 },
  inspector: { borderRadius: 8, backgroundColor: colors.surfaceRaised, paddingVertical: 8 },
  navigation: { flexDirection: "row", alignItems: "center", gap: 4 }, description: { flex: 1, color: colors.muted, fontSize: 11, lineHeight: 17 },
  arrow: { width: 30, minHeight: 44, alignItems: "center", justifyContent: "center" }, arrowText: { color: colors.text, fontSize: 26 }, disabled: { opacity: .25 }
});
