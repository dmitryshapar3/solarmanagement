import { useEffect, useMemo, useRef, useState } from "react";
import { Pressable, View, type GestureResponderEvent } from "react-native";
import Svg, { Circle, G, Path, Text as SvgText } from "react-native-svg";
import { useLanguage } from "../../application/LanguageContext";
import { ThemedText as Text } from "../../core/components";
import { formatNumber } from "../../core/format";
import { formattingLocale } from "../../core/i18n";
import { useTheme } from "../theme/ThemeProvider";
import { DEFAULT_ROOF_CAMERA, diagramRoofs, normalizeRoofCamera, orbitRoofGesture, projectRoofPoint, roofMesh, roofPath3, sunDay, sunPoint3, type OrbitCamera, type OrbitTouch, type Point3, type RoofSunSite } from "./roofSunGeometry";

const average = (points: Point3[]): Point3 => ({ x: points.reduce((v, p) => v + p.x, 0) / points.length, y: points.reduce((v, p) => v + p.y, 0) / points.length, z: points.reduce((v, p) => v + p.z, 0) / points.length });
const between = (a: Point3, b: Point3, t: number): Point3 => ({ x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t, z: a.z + (b.z - a.z) * t });

export function RoofSunDiagram({ site, at }: { site: RoofSunSite; at?: number }) {
  const { colors } = useTheme(), { t } = useLanguage();
  const [clock, setClock] = useState(() => Date.now()), instant = at ?? clock;
  // Camera gestures must not recompute the entire local solar day every frame.
  useEffect(() => { if (at !== undefined) return; const timer = setInterval(() => setClock(Date.now()), 60000); return () => clearInterval(timer); }, [at]);
  const [camera, setCamera] = useState<OrbitCamera>({ ...DEFAULT_ROOF_CAMERA });
  const touches = useRef<OrbitTouch[]>([]);
  const day = useMemo(() => sunDay(site.latitude, site.longitude, site.timeZoneId, instant), [site.latitude, site.longitude, site.timeZoneId, instant]);
  const mesh = useMemo(() => roofMesh(site), [site.roof1Kwp, site.roof1Tilt, site.roof1Azimuth, site.roof2Kwp, site.roof2Tilt, site.roof2Azimuth]);
  const roofs = diagramRoofs(site), date = day ? new Intl.DateTimeFormat(formattingLocale(), { timeZone: "UTC", day: "numeric", month: "short", year: "numeric" }).format(new Date(`${day.date}T12:00:00Z`)) : "";
  const time = (value: number | null) => value !== null && day ? new Intl.DateTimeFormat(formattingLocale(), { timeZone: day.timeZoneId, hour: "2-digit", minute: "2-digit" }).format(value) : "—";
  const description = [t("Roof layout and calculated sun path"), day ? t("Sun path · {0}", date) : t("Enter valid coordinates and a solar time zone to see the sun path."), ...roofs.map(roof => `${t(roof.number === 1 ? "Roof 1" : "Roof 2")}: ${formatNumber(roof.capacity, 2)} kWp · ${t("Azimuth")} ${formatNumber(roof.azimuth, 1)}° · ${t("Tilt")} ${formatNumber(roof.tilt, 1)}°`)].join(". ");
  const adjust = (change: Partial<OrbitCamera>) => setCamera(current => normalizeRoofCamera({ ...current, ...change }));
  const eventTouches = (event: GestureResponderEvent) => event.nativeEvent.touches.map(p => ({ pageX: p.pageX, pageY: p.pageY }));
  const move = (event: GestureResponderEvent) => { const next = eventTouches(event), previous = touches.current; touches.current = next; setCamera(current => orbitRoofGesture(current, previous, next)); };
  const clear = () => { touches.current = []; };
  const yaw = camera.yaw * Math.PI / 180, elevation = camera.elevation * Math.PI / 180;
  const viewDirection = { x: Math.sin(yaw) * Math.cos(elevation), y: Math.cos(yaw) * Math.cos(elevation), z: Math.sin(elevation) };
  const visible = mesh.faces.filter(face => face.kind === "vertical" || face.normal.x * viewDirection.x + face.normal.y * viewDirection.y + face.normal.z * viewDirection.z > 1e-8)
    .sort((a, b) => projectRoofPoint(average(a.points), camera).z - projectRoofPoint(average(b.points), camera).z);
  const horizon = Array.from({ length: 73 }, (_, i) => sunPoint3({ azimuth: i * 5, elevation: 0 }));
  const button = (label: string, symbol: string, onPress: () => void, disabled = false) => <Pressable key={label} accessibilityRole="button" accessibilityLabel={t(label)} accessibilityState={{ disabled }} disabled={disabled} onPress={onPress} style={({ pressed }) => ({ minWidth: 44, minHeight: 44, paddingHorizontal: 12, borderRadius: 14, alignItems: "center", justifyContent: "center", borderWidth: 1, borderColor: colors.line, backgroundColor: pressed ? colors.fill : colors.surface, opacity: disabled ? .4 : 1 })}><Text style={{ fontSize: symbol.length === 1 ? 22 : 13, fontWeight: "600" }}>{symbol}</Text></Pressable>;
  return <View testID="roof-sun-diagram" style={{ gap: 14, borderRadius: 24, padding: 12, backgroundColor: colors.bg }}>
    <Text style={{ fontSize: 17, fontWeight: "600" }}>{t("Roof and sun")}</Text>
    {day ? <Text style={{ fontSize: 13, color: colors.ink2 }}>{t("Sun path · {0}", date)} · {site.timeZoneId}</Text> : null}
    <View testID="roof-sun-orbit" accessible accessibilityRole="adjustable" accessibilityLabel={description} accessibilityHint={t("Drag to rotate · Pinch to zoom")} accessibilityValue={{ min: 70, max: 250, now: Math.round(camera.zoom * 100), text: `${Math.round(camera.zoom * 100)}%` }} accessibilityActions={[{ name: "increment", label: t("Zoom in") }, { name: "decrement", label: t("Zoom out") }]} onAccessibilityAction={event => setCamera(current => normalizeRoofCamera({ ...current, zoom: current.zoom + (event.nativeEvent.actionName === "increment" ? .2 : -.2) }))}
      onStartShouldSetResponder={() => true} onMoveShouldSetResponder={() => true} onResponderTerminationRequest={() => false} onResponderGrant={event => { touches.current = eventTouches(event); }} onResponderStart={event => { touches.current = eventTouches(event); }} onResponderMove={move} onResponderEnd={event => { touches.current = eventTouches(event); }} onResponderRelease={clear} onResponderTerminate={clear} style={{ borderRadius: 20, overflow: "hidden", backgroundColor: colors.surface }}>
      <Svg width="100%" height={320} viewBox="0 0 320 320" accessible={false}>
        <Path d={roofPath3(horizon, camera, true)} fill={colors.bg} stroke={colors.line} strokeWidth={1.5} />
        <Path d={roofPath3([{ x: -110, y: 0, z: 0 }, { x: 110, y: 0, z: 0 }], camera) + " " + roofPath3([{ x: 0, y: -110, z: 0 }, { x: 0, y: 110, z: 0 }], camera)} fill="none" stroke={colors.line} strokeDasharray="3 5" />
        {[{ label: "N", azimuth: 0 }, { label: "E", azimuth: 90 }, { label: "S", azimuth: 180 }, { label: "W", azimuth: 270 }].map(point => { const p = projectRoofPoint(sunPoint3({ ...point, elevation: 0 }, 124), camera); return <SvgText key={point.label} x={p.x} y={p.y + 5} fill={colors.ink2} fontSize={14} fontWeight="700" textAnchor="middle">{point.label}</SvgText>; })}
        {day?.paths.map((path, index) => <G key={index}>
          <Path testID="roof-sun-path" d={roofPath3(path.map(p => sunPoint3(p)), camera)} stroke={colors.solar} strokeWidth={2.5} strokeLinecap="round" strokeLinejoin="round" fill="none" />
          {path.length > 4 ? [1 / 3, 2 / 3].map(fraction => { const position = Math.min(path.length - 2, Math.floor(path.length * fraction)), point = projectRoofPoint(sunPoint3(path[position]!), camera), next = projectRoofPoint(sunPoint3(path[position + 1]!), camera);
            return <Path key={fraction} testID="roof-sun-direction" d="M-5 -4L0 0L-5 4" transform={`translate(${point.x} ${point.y}) rotate(${Math.atan2(next.y - point.y, next.x - point.x) * 180 / Math.PI})`} stroke={colors.solar} strokeWidth={2.5} strokeLinecap="round" strokeLinejoin="round" fill="none" />;
          }) : null}
        </G>)}
        {mesh.faces.length ? <Path d={roofPath3(mesh.footprint.map(p => ({ ...p, x: p.x * 1.07, y: p.y * 1.07 })), camera, true)} fill={colors.ink} opacity={.09} /> : null}
        {visible.map(face => {
          const centroid = projectRoofPoint(average(face.points), camera), light = Math.abs(face.normal.x * .4 + face.normal.y * -.5 + face.normal.z * .8);
          return <G key={face.id} testID={face.roofNumber ? `roof-sun-roof-${face.roofNumber}` : face.id}>
            <Path testID={`roof-sun-face-${face.id}`} d={roofPath3(face.points, camera, true)} fill={face.kind === "wall" ? "#e1ded5" : face.roofNumber === 2 ? "#91604d" : "#a8725b"} stroke="#6b4c3d" strokeWidth={.7} strokeLinejoin="round" />
            <Path d={roofPath3(face.points, camera, true)} fill="#12120f" opacity={face.kind === "wall" ? .04 + .14 * (1 - light) : .08 + .2 * (1 - light)} />
            {face.kind === "wall" && Math.hypot(face.points[1]!.x - face.points[0]!.x, face.points[1]!.y - face.points[0]!.y) > 18 && Math.min(face.points[2]!.z, face.points[3]!.z) > 5 ? [.22, .62].map(u => {
              const left = between(face.points[0]!, face.points[1]!, u), right = between(face.points[0]!, face.points[1]!, u + .16), topLeft = between(face.points[3]!, face.points[2]!, u), topRight = between(face.points[3]!, face.points[2]!, u + .16);
              return <Path key={u} d={roofPath3([between(left, topLeft, .3), between(right, topRight, .3), between(right, topRight, .66), between(left, topLeft, .66)], camera, true)} fill="#456775" stroke="#89b2c3" strokeWidth={.6} />;
            }) : null}
            {face.panels.length ? <Path d={roofPath3(face.panels, camera, true)} fill="#234356" stroke="#89b2c3" strokeWidth={.8} strokeLinejoin="round" /> : null}
            {face.grid.map((line, index) => <Path key={index} d={roofPath3(line, camera)} stroke="#89b2c3" strokeWidth={.6} opacity={.8} fill="none" />)}
            {face.roofNumber ? <G transform={`translate(${centroid.x} ${centroid.y})`}><Circle r={8} fill={colors.sun} stroke={colors.onSun} strokeWidth={.8} /><SvgText y={3.5} fill={colors.onSun} textAnchor="middle" fontSize={10} fontWeight="700">{face.roofNumber}</SvgText></G> : null}
          </G>;
        })}
        {mesh.ridge.map((line, index) => <Path key={index} testID="roof-sun-ridge" d={roofPath3(line, camera)} stroke={colors.ink3} strokeWidth={1.5} fill="none" />)}
        {day?.now ? (() => { const point = projectRoofPoint(sunPoint3(day.now), camera); return <G testID="roof-sun-current" transform={`translate(${point.x} ${point.y})`}><Circle r={10} fill={colors.sun} stroke={colors.onSun} strokeWidth={1.5} /><Path d="M0 -16V-13M0 13V16M-16 0H-13M13 0H16M-11 -11L-9 -9M9 9L11 11M-11 11L-9 9M9 -9L11 -11" fill="none" stroke={colors.solar} strokeWidth={2} strokeLinecap="round" /></G>; })() : null}
      </Svg>
    </View>
    <Text style={{ fontSize: 13, color: colors.ink2, textAlign: "center" }}>{t("Drag to rotate · Pinch to zoom")}</Text>
    <View style={{ flexDirection: "row", justifyContent: "center", flexWrap: "wrap", gap: 8 }}>
      {button("Zoom out", "−", () => adjust({ zoom: camera.zoom - .2 }), camera.zoom <= .7)}
      {button("Reset view", t("Reset view"), () => setCamera({ ...DEFAULT_ROOF_CAMERA }))}
      {button("Zoom in", "+", () => adjust({ zoom: camera.zoom + .2 }), camera.zoom >= 2.5)}
    </View>
    <View style={{ flexDirection: "row", justifyContent: "center", gap: 8 }}>
      {button("Rotate left", "←", () => adjust({ yaw: camera.yaw - 15 }))}{button("Rotate right", "→", () => adjust({ yaw: camera.yaw + 15 }))}
      {button("Tilt view up", "↑", () => adjust({ elevation: camera.elevation + 10 }), camera.elevation >= 85)}{button("Tilt view down", "↓", () => adjust({ elevation: camera.elevation - 10 }), camera.elevation <= 10)}
    </View>
    {day ? <View style={{ flexDirection: "row", justifyContent: "center", flexWrap: "wrap", gap: 10 }}>
      {day.state === "polar-day" ? <Text style={{ fontSize: 13, color: colors.ink2 }}>{t("Sun stays above the horizon on this date.")}</Text> : day.state === "polar-night" ? <Text style={{ fontSize: 13, color: colors.ink2 }}>{t("Sun stays below the horizon on this date.")}</Text> : <><Text style={{ fontSize: 13, color: colors.ink2 }}>{t("Sunrise")} {time(day.sunrise)}</Text><Text style={{ fontSize: 13, color: colors.ink2 }}>{t("Sunset")} {time(day.sunset)}</Text></>}
      <Text style={{ fontSize: 13, color: colors.ink2 }}>{t(day.now ? "Sun now" : "Sun below horizon")}{day.now ? ` ${formatNumber(day.now.elevation, 0)}°` : ""}</Text>
    </View> : <Text accessibilityRole="alert" style={{ fontSize: 13, color: colors.ink2 }}>{t("Enter valid coordinates and a solar time zone to see the sun path.")}</Text>}
    <View style={{ flexDirection: "row", flexWrap: "wrap", gap: 8 }}>
      {roofs.map(roof => <View key={roof.number} style={{ flex: 1, minWidth: 110, borderRadius: 16, padding: 10, gap: 4, backgroundColor: colors.surface }}>
        <Text style={{ fontSize: 13, fontWeight: "600" }}>{t(roof.number === 1 ? "Roof 1" : "Roof 2")} · {formatNumber(roof.capacity, 2)} kWp</Text>
        <Text style={{ fontSize: 13, color: colors.ink2 }}>{t("Azimuth")} {formatNumber(roof.azimuth, 1)}° · {t("Tilt")} {formatNumber(roof.tilt, 1)}°</Text>
      </View>)}
    </View>
    {mesh.hasVerticalPanels ? <Text style={{ fontSize: 13, lineHeight: 18, color: colors.ink3 }}>{t("Vertical arrays are shown standing on the schematic roof.")}</Text> : null}
    <Text style={{ fontSize: 13, lineHeight: 18, color: colors.ink3 }}>{t("Schematic roof; dimensions and shadows are not modelled.")}</Text>
  </View>;
}
