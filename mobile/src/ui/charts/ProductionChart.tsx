import { useMemo, useState } from "react";
import { View } from "react-native";
import Svg, { Circle, Line, Path, Rect, Text as SvgText } from "react-native-svg";
import { useLanguage } from "../../application/LanguageContext";
import { ThemedText as Text } from "../../core/components";
import { formattingLocale } from "../../core/i18n";
import { useTheme } from "../theme/ThemeProvider";
import { chartGeometry, chartWidth, chartHeight, type ChartPoint, known } from "../../features/energy/chartPolicy";
export function ProductionChart({ points, unit = "kW", bars = false, compact = false, now, selectedIndex, onSelect, hero = false }: {
  points: ChartPoint[]; unit?: string; bars?: boolean; compact?: boolean; now?: string | null; selectedIndex?: number; onSelect?: (index: number) => void; hero?: boolean;
}) {
  const { colors } = useTheme(); const { t } = useLanguage(); const [width, setWidth] = useState(326);
  const lineColor = hero ? colors.onSun : colors.solar;
  const latest = Math.max(0, points.findLastIndex(p => known(p.actual)));
  const [selection, setSelection] = useState(latest); const selected = Math.max(0, Math.min(points.length - 1, selectedIndex ?? selection));
  const geometry = useMemo(() => chartGeometry(points, "generation"), [points]); const point = points[selected];
  const pick = (index: number) => { const safe = Math.max(0, Math.min(points.length - 1,index)); setSelection(safe); onSelect?.(safe); };
  const scrub = (x: number) => pick(Math.round(((x / width) * chartWidth - 38) / geometry.step - .5));
  const ticks = [...new Set(Array.from({ length: Math.min(5,points.length) },(_,i)=>Math.round(i*(points.length-1)/Math.max(1,Math.min(5,points.length)-1))))];
  const nowIndex = now && points[0] && !bars ? (Date.parse(now)-Date.parse(points[0].timestamp))/3600000-.5 : null;
  const maxValue = Math.max(1,...points.flatMap(p=>[p.actual,p.possible?.upperKw]).filter(known));
  const maximum = Math.ceil(maxValue); const y = (value: number) => 176 - value/maximum*158;
  return <View style={{ gap: 10 }}>
    {!compact && point ? <Text accessibilityLiveRegion="polite" style={{ fontSize: 13, lineHeight: 18, color: colors.ink2 }}>{point.description}</Text> : null}
    <View onLayout={event=>setWidth(event.nativeEvent.layout.width)} accessibilityRole="adjustable" accessibilityLabel={t("Production chart in {0}",unit)} accessibilityValue={{ text: point?.description ?? t("No data") }}
      accessibilityActions={[{name:"increment",label:t("Next interval")},{name:"decrement",label:t("Previous interval")}]} onAccessibilityAction={event=>pick(selected+(event.nativeEvent.actionName==="increment"?1:-1))}
      onStartShouldSetResponder={()=>!compact} onMoveShouldSetResponder={()=>!compact} onResponderGrant={event=>scrub(event.nativeEvent.locationX)} onResponderMove={event=>scrub(event.nativeEvent.locationX)}>
      <Svg width="100%" height={compact ? 108 : chartHeight} viewBox={`0 0 ${chartWidth} ${chartHeight}`}>
        {geometry.ticks.map((tick,i)=><Line key={i} x1={38} x2={348} y1={tick.y} y2={tick.y} stroke={hero ? colors.onSun : colors.line} strokeOpacity={hero ? .15 : 1} strokeWidth={.6} />)}
        {!compact ? geometry.ticks.map((tick,i)=><SvgText key={`y${i}`} x={32} y={tick.y+4} fill={colors.ink3} fontSize={12} fontFamily="Onest-Medium" textAnchor="end">{tick.value.toLocaleString(formattingLocale(),{maximumFractionDigits:1})}</SvgText>) : null}
        {!compact ? <SvgText x={38} y={11} fill={colors.ink3} fontSize={12} fontFamily="Onest-Medium">{unit}</SvgText> : null}
        {!compact && point ? <Rect x={geometry.x(selected)-geometry.step/2} y={18} width={geometry.step} height={158} rx={4} fill={colors.selectedColumn} /> : null}
        {bars ? points.map((p,i)=><ReactBar key={p.timestamp} x={geometry.x(i)} width={Math.max(2,geometry.step*.58)} actual={p.actual} lower={p.possible?.lowerKw} upper={p.possible?.upperKw} y={y} selected={i===selected} />) : <>
          {geometry.bandPaths.map((path,i)=><Path key={`band${i}`} d={path} fill={hero ? colors.onSun : colors.expectedBand} fillOpacity={hero ? .12 : 1} />)}
          {geometry.actualPaths.map((path,i)=><Path key={`actual${i}`} d={path} fill="none" stroke={lineColor} strokeWidth={2.5} strokeLinecap="round" strokeLinejoin="round" />)}
          {geometry.actualDots.map((dot,i)=><Circle key={`dot${i}`} cx={dot.x} cy={dot.y} r={1.6} fill={lineColor} />)}
          {!compact && point && known(point.actual) ? <Circle cx={geometry.x(selected)} cy={y(point.actual)} r={5} fill={colors.surface} stroke={colors.solar} strokeWidth={2.5} /> : null}
          {nowIndex !== null && nowIndex>=0 && nowIndex<=points.length ? <><Line x1={geometry.x(nowIndex)} x2={geometry.x(nowIndex)} y1={18} y2={176} stroke={colors.ink3} strokeDasharray="4 4" />{!compact ? <SvgText x={Math.min(325,geometry.x(nowIndex)+4)} y={28} fill={colors.ink2} fontFamily="Onest-SemiBold" fontSize={12}>{t("Now")}</SvgText> : null}</> : null}
        </>}
        {!compact ? ticks.map(i=><SvgText key={`x${i}`} x={geometry.x(i)} y={198} fill={colors.ink3} fontSize={12} fontFamily="Onest-Medium" textAnchor="middle">{points[i]?.label}</SvgText>) : null}
      </Svg>
    </View>
  </View>;
}
function ReactBar({ x,width,actual,lower,upper,y,selected }: { x:number;width:number;actual?:number|null;lower?:number;upper?:number;y:(value:number)=>number;selected:boolean }) {
  const { colors } = useTheme(); return <>{known(lower)&&known(upper) ? <Rect x={x-width/2} y={y(upper)} width={width} height={Math.max(1,y(lower)-y(upper))} fill={colors.expectedBand} rx={4} /> : null}{known(actual)?<Rect x={x-width/2} y={y(actual)} width={width} height={Math.max(1.5,176-y(actual))} rx={4} fill={colors.solar} opacity={selected?1:.75}/>:null}</>;
}
