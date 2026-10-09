import { useMemo } from "react";
import { View } from "react-native";
import Svg, { Circle, G, Line, Path, Rect, Text as SvgText } from "react-native-svg";
import { useLanguage } from "../../application/LanguageContext";
import { ThemedText as Text } from "../../core/components";
import { formatNumber } from "../../core/format";
import { formattingLocale } from "../../core/i18n";
import { useTheme } from "../theme/ThemeProvider";
import { diagramRoofs, sunDay, sunPath, sunPosition, type RoofSunSite } from "./roofSunGeometry";

export function RoofSunDiagram({ site, at }: { site: RoofSunSite; at?: number }) {
  const { colors } = useTheme(); const { t } = useLanguage(); const instant = at ?? Date.now();
  const day = useMemo(() => sunDay(site.latitude, site.longitude, site.timeZoneId, instant), [site.latitude, site.longitude, site.timeZoneId, instant]);
  const roofs = diagramRoofs(site), date = day ? new Intl.DateTimeFormat(formattingLocale(), { timeZone: "UTC", day: "numeric", month: "short", year: "numeric" }).format(new Date(`${day.date}T12:00:00Z`)) : "";
  const time = (value: number | null) => value !== null && day ? new Intl.DateTimeFormat(formattingLocale(), { timeZone: day.timeZoneId, hour: "2-digit", minute: "2-digit" }).format(value) : "—";
  const description = [t("Roof layout and calculated sun path"), day ? t("Sun path · {0}", date) : t("Enter valid coordinates and a solar time zone to see the sun path."), ...roofs.map(roof => `${t(roof.number === 1 ? "Roof 1" : "Roof 2")}: ${formatNumber(roof.capacity, 2)} kWp · ${t("Azimuth")} ${formatNumber(roof.azimuth, 1)}° · ${t("Tilt")} ${formatNumber(roof.tilt, 1)}°`)].join(". ");
  return <View testID="roof-sun-diagram" style={{ gap: 14, borderRadius: 24, padding: 12, backgroundColor: colors.bg }}>
    <Text style={{ fontSize: 17, fontWeight: "600" }}>{t("Roof and sun")}</Text>
    {day ? <>
      <Text style={{ fontSize: 13, color: colors.ink2 }}>{t("Sun path · {0}", date)} · {site.timeZoneId}</Text>
      <Svg width="100%" height={300} viewBox="0 0 320 320" accessible accessibilityRole="image" accessibilityLabel={description}>
        <Circle cx={160} cy={160} r={112} fill={colors.surface} stroke={colors.line} strokeWidth={1.5} />
        <Circle cx={160} cy={160} r={56} fill="none" stroke={colors.line} strokeDasharray="3 6" />
        <Path d="M160 48V272M48 160H272" stroke={colors.line} fill="none" />
        {[{ label: "N", x: 160, y: 27 }, { label: "E", x: 295, y: 165 }, { label: "S", x: 160, y: 304 }, { label: "W", x: 25, y: 165 }].map(point => <SvgText key={point.label} x={point.x} y={point.y} fill={colors.ink} fontSize={15} fontWeight="700" textAnchor="middle">{point.label}</SvgText>)}
        <SvgText x={165} y={63} fill={colors.ink3} fontSize={12}>0°</SvgText><SvgText x={165} y={111} fill={colors.ink3} fontSize={12}>45°</SvgText>
        {day.paths.map((path, index) => <G key={index}>
          <Path testID="roof-sun-path" d={sunPath(path)} stroke={colors.solar} strokeWidth={3} strokeLinecap="round" strokeLinejoin="round" fill="none" />
          {path.length > 4 ? [1 / 3, 2 / 3].map(fraction => { const position = Math.min(path.length - 2, Math.floor(path.length * fraction)), point = path[position]!, next = path[position + 1]!;
            return <Path key={fraction} testID="roof-sun-direction" d="M-5 -4L0 0L-5 4" transform={`translate(${point.x} ${point.y}) rotate(${Math.atan2(next.y - point.y, next.x - point.x) * 180 / Math.PI})`} stroke={colors.solar} strokeWidth={3} strokeLinecap="round" strokeLinejoin="round" fill="none" />;
          }) : null}
        </G>)}
        {roofs.map(roof => <G key={roof.number} testID={`roof-sun-roof-${roof.number}`} transform={`translate(${roof.centerX} 160)`}>
          <G testID={`roof-sun-bearing-${roof.number}`} rotation={roof.azimuth}>
            <Path d="M0 -12V-61M-5 -54L0 -61L5 -54" stroke={colors.ink} strokeWidth={1.75} strokeDasharray="4 3" strokeLinecap="round" strokeLinejoin="round" fill="none" />
            <Rect x={-roof.width / 2} y={-roof.depth / 2} width={roof.width} height={roof.depth} rx={3} fill={roof.number === 1 ? colors.ink : colors.ink2} stroke={colors.surface} strokeWidth={2} />
            <Path d={`M${-roof.width / 6} ${-roof.depth / 2}V${roof.depth / 2}M${roof.width / 6} ${-roof.depth / 2}V${roof.depth / 2}M${-roof.width / 2} 0H${roof.width / 2}`} stroke={colors.bg} strokeWidth={1} opacity={.65} fill="none" />
          </G><Circle r={10} fill={colors.surface} stroke={colors.line} /><SvgText y={4} fill={colors.ink} textAnchor="middle" fontSize={12} fontWeight="700">{roof.number}</SvgText>
        </G>)}
        {[day.sunrise, day.sunset].filter(value => value !== null).map((value, index) => { const point = sunPosition(site.latitude, site.longitude, value!); return <Circle key={index} cx={point.x} cy={point.y} r={4} fill={colors.surface} stroke={colors.solar} strokeWidth={2} />; })}
        {day.now ? <G testID="roof-sun-current" transform={`translate(${day.now.x} ${day.now.y})`}><Circle r={11} fill={colors.sun} stroke={colors.onSun} strokeWidth={1.5} /><Path d="M0 -17V-14M0 14V17M-17 0H-14M14 0H17M-12 -12L-10 -10M10 10L12 12M-12 12L-10 10M10 -10L12 -12" fill="none" stroke={colors.solar} strokeWidth={2} strokeLinecap="round" /></G> : null}
      </Svg>
      <View style={{ flexDirection: "row", justifyContent: "center", flexWrap: "wrap", gap: 10 }}>
        {day.state === "polar-day" ? <Text style={{ fontSize: 13, color: colors.ink2 }}>{t("Sun stays above the horizon on this date.")}</Text> : day.state === "polar-night" ? <Text style={{ fontSize: 13, color: colors.ink2 }}>{t("Sun stays below the horizon on this date.")}</Text> : <><Text style={{ fontSize: 13, color: colors.ink2 }}>{t("Sunrise")} {time(day.sunrise)}</Text><Text style={{ fontSize: 13, color: colors.ink2 }}>{t("Sunset")} {time(day.sunset)}</Text></>}
        <Text style={{ fontSize: 13, color: colors.ink2 }}>{t(day.now ? "Sun now" : "Sun below horizon")}{day.now ? ` ${formatNumber(day.now.elevation, 0)}°` : ""}</Text>
      </View>
    </> : <Text accessibilityRole="alert" style={{ fontSize: 13, color: colors.ink2 }}>{t("Enter valid coordinates and a solar time zone to see the sun path.")}</Text>}
    <View style={{ flexDirection: "row", gap: 8 }}>
      {roofs.map(roof => <View key={roof.number} style={{ flex: 1, minWidth: 0, borderRadius: 16, padding: 10, gap: 4, backgroundColor: colors.surface }}>
        <Text style={{ fontSize: 13, fontWeight: "600" }}>{t(roof.number === 1 ? "Roof 1" : "Roof 2")} · {formatNumber(roof.capacity, 2)} kWp</Text>
        <Svg width="100%" height={76} viewBox="0 0 144 76" accessible={false}><Path d="M14 60H94" stroke={colors.line} /><G testID={`roof-sun-profile-${roof.number}`} transform={`translate(20 60) rotate(${-roof.tilt})`}><Rect x={0} y={-5} width={60} height={6} rx={2} fill={colors.ink} /><Path d="M20 -5V1M40 -5V1" stroke={colors.bg} opacity={.65} /></G><SvgText x={106} y={60} fill={colors.ink2} fontSize={13}>{formatNumber(roof.tilt, 1)}°</SvgText></Svg>
        <Text style={{ fontSize: 13, color: colors.ink2 }}>{t("Azimuth")} {formatNumber(roof.azimuth, 1)}° · {t("Tilt")} {formatNumber(roof.tilt, 1)}°</Text>
      </View>)}
    </View>
    <Text style={{ fontSize: 13, lineHeight: 18, color: colors.ink3 }}>{t("Calculated geometric sun path; terrain and shadows are not included.")}</Text>
  </View>;
}
