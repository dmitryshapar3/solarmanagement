import { useMemo, useState } from "react";
import { View } from "react-native";
import Svg, { Line, Rect, Text as SvgText } from "react-native-svg";
import { useLanguage } from "../../application/LanguageContext";
import { ThemedText as Text } from "../../core/components";
import { formattingLocale } from "../../core/i18n";
import { chartGeometry, chartWidth, chartHeight, type ChartPoint, known } from "../../features/energy/chartPolicy";
import { useTheme } from "../theme/ThemeProvider";

export function ExportChart({ points, unit }: { points: ChartPoint[]; unit: string }) {
  const { colors } = useTheme(); const { t } = useLanguage(); const [width, setWidth] = useState(326);
  const [selection, setSelection] = useState(Math.max(0, points.findLastIndex(point => known(point.provisional) || known(point.completed))));
  const selected = Math.max(0, Math.min(points.length - 1, selection)); const point = points[selected];
  const geometry = useMemo(() => chartGeometry(points, "sales"), [points]);
  const pick = (x: number) => setSelection(Math.max(0, Math.min(points.length - 1, Math.round((x / width * chartWidth - 38) / geometry.step - .5))));
  const ticks = [...new Set(Array.from({ length: Math.min(5, points.length) }, (_, i) => Math.round(i * (points.length - 1) / Math.max(1, Math.min(5, points.length) - 1))))];
  return <View style={{ gap: 10 }}><Text accessibilityLiveRegion="polite" style={{ fontSize: 13, lineHeight: 18, color: colors.ink2 }}>{point?.description ?? t("No data")}</Text>
    <View onLayout={event => setWidth(event.nativeEvent.layout.width)} accessibilityRole="adjustable" accessibilityLabel={t("Export chart in {0}", unit)} accessibilityValue={{ text: point?.description ?? t("No data") }} accessibilityActions={[{ name: "increment", label: t("Next interval") }, { name: "decrement", label: t("Previous interval") }]} onAccessibilityAction={event => setSelection(Math.max(0, Math.min(points.length - 1, selected + (event.nativeEvent.actionName === "increment" ? 1 : -1))))} onStartShouldSetResponder={() => true} onMoveShouldSetResponder={() => true} onResponderGrant={event => pick(event.nativeEvent.locationX)} onResponderMove={event => pick(event.nativeEvent.locationX)}>
      <Svg width="100%" height={chartHeight} viewBox={`0 0 ${chartWidth} ${chartHeight}`}>
        {geometry.ticks.map((tick, i) => <Line key={i} x1={38} x2={348} y1={tick.y} y2={tick.y} stroke={colors.line} strokeWidth={.6} />)}
        {geometry.ticks.map((tick, i) => <SvgText key={`y${i}`} x={32} y={tick.y + 4} fill={colors.ink3} fontSize={12} fontFamily="Onest-Medium" textAnchor="end">{tick.value.toLocaleString(formattingLocale(), { maximumFractionDigits: 1 })}</SvgText>)}
        <SvgText x={38} y={11} fill={colors.ink3} fontFamily="Onest-Medium" fontSize={12}>{unit}</SvgText>
        {point ? <Rect x={geometry.x(selected) - geometry.step / 2} y={18} height={158} width={geometry.step} rx={4} fill={colors.selectedColumn} /> : null}
        {geometry.bars.map((bar, i) => <Rect key={i} x={bar.x} y={bar.y} width={bar.width} height={bar.height} rx={3} fill={bar.provisional ? colors.gridTint : colors.grid} stroke={bar.provisional ? colors.grid : "none"} strokeWidth={bar.provisional ? 1.5 : 0} opacity={bar.index === selected ? 1 : .7} />)}
        <Line x1={38} x2={348} y1={geometry.baseline} y2={geometry.baseline} stroke={colors.lineStrong} strokeWidth={1} />
        {ticks.map(i => <SvgText key={`x${i}`} x={geometry.x(i)} y={198} fill={colors.ink3} fontSize={12} fontFamily="Onest-Medium" textAnchor="middle">{points[i]?.label}</SvgText>)}
      </Svg>
    </View>
  </View>;
}
