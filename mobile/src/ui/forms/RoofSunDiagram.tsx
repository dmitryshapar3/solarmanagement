import { useEffect, useMemo, useRef, useState } from "react";
import { Pressable, View, type GestureResponderEvent } from "react-native";
import Svg, { Circle, G, Path, Text as SvgText } from "react-native-svg";
import { useLanguage } from "../../application/LanguageContext";
import { ThemedText as Text } from "../../core/components";
import { formatNumber } from "../../core/format";
import { formattingLocale } from "../../core/i18n";
import { useTheme } from "../theme/ThemeProvider";
import { diagramRoofs, fixedGableRoofCamera, normalizeRoofCamera, normalizeSunRotationAngle, orbitRoofGesture, projectRoofPoint, projectSunFramePoint, roofCameraFromView, roofMesh, roofPath3, sunDay, sunFramePath3, sunPoint3, type OrbitCamera, type OrbitTouch, type Point3, type RoofSunSite, type SolarRotation } from "./roofSunGeometry";

const average = (points: Point3[]): Point3 => ({ x: points.reduce((v, p) => v + p.x, 0) / points.length, y: points.reduce((v, p) => v + p.y, 0) / points.length, z: points.reduce((v, p) => v + p.z, 0) / points.length });
const between = (a: Point3, b: Point3, t: number): Point3 => ({ x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t, z: a.z + (b.z - a.z) * t });

const initialView: OrbitCamera = { yaw: 0, elevation: 32, zoom: 1 };

export function RoofSunDiagram({ site, at, onOrientationChange, directionDisabled = false }: { site: RoofSunSite; at?: number; onOrientationChange?: (deltaDegrees: number) => void; directionDisabled?: boolean }) {
  const { colors } = useTheme(), { t } = useLanguage();
  const [clock, setClock] = useState(() => Date.now()), instant = at ?? clock;
  // Camera gestures must not recompute the entire local solar day every frame.
  useEffect(() => { if (at !== undefined) return; const timer = setInterval(() => setClock(Date.now()), 60000); return () => clearInterval(timer); }, [at]);
  const [view, setView] = useState<OrbitCamera>(initialView), [sunAngles, setSunAngles] = useState<SolarRotation>({ x: 0, y: 0, z: 0 });
  const frontYaw = fixedGableRoofCamera(site).yaw, camera = roofCameraFromView(site, view);
  const [orientationMode, setOrientationMode] = useState(false);
  const settingDirection = orientationMode && Boolean(onOrientationChange);
  const orientation = useRef({ enabled: settingDirection, change: onOrientationChange, disabled: directionDisabled });
  orientation.current = { enabled: settingDirection, change: onOrientationChange, disabled: directionDisabled };
  const mounted = useRef(true);
  const touches = useRef<OrbitTouch[]>([]);
  const gestureActive = useRef(false);
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; gestureActive.current = false; touches.current = []; }; }, []);
  const day = useMemo(() => sunDay(site.latitude, site.longitude, site.timeZoneId, instant), [site.latitude, site.longitude, site.timeZoneId, instant]);
  const mesh = useMemo(() => roofMesh(site), [site.roof1Kwp, site.roof1Tilt, site.roof1Azimuth, site.roof2Kwp, site.roof2Tilt, site.roof2Azimuth, site.roof1PanelCount, site.roof2PanelCount, site.roof1PanelsPerRow, site.roof2PanelsPerRow]);
  const roofs = diagramRoofs(site), date = day ? new Intl.DateTimeFormat(formattingLocale(), { timeZone: "UTC", day: "numeric", month: "short", year: "numeric" }).format(new Date(`${day.date}T12:00:00Z`)) : "";
  const time = (value: number | null) => value !== null && day ? new Intl.DateTimeFormat(formattingLocale(), { timeZone: day.timeZoneId, hour: "2-digit", minute: "2-digit" }).format(value) : "—";
  const description = [t("Roof layout and calculated sun path"), day ? t("Sun path · {0}", date) : t("Enter valid coordinates and a solar time zone to see the sun path."), ...roofs.map(roof => `${t(roof.number === 1 ? "Roof 1" : "Roof 2")}: ${formatNumber(roof.capacity, 2)} kWp · ${t("Azimuth")} ${formatNumber(roof.azimuth, 1)}° · ${t("Tilt")} ${formatNumber(roof.tilt, 1)}°${roof.panelCount != null ? ` · ${t("{0} panels", roof.panelCount)}` : ""}`)].join(". ");
  const adjustView = (delta: Partial<OrbitCamera>) => { if (mounted.current) setView(current => normalizeRoofCamera({ yaw: current.yaw + (delta.yaw ?? 0), elevation: current.elevation + (delta.elevation ?? 0), zoom: current.zoom + (delta.zoom ?? 0) })); };
  const adjustZoom = (delta: number) => adjustView({ zoom: delta });
  const eventTouches = (event: GestureResponderEvent) => event.nativeEvent.touches.map(p => ({ pageX: p.pageX, pageY: p.pageY }));
  const rotateSun = (axis: keyof SolarRotation, delta: number) => {
    const current = orientation.current;
    if (!mounted.current || current.disabled || !Number.isFinite(delta) || !delta) return;
    if (axis === "y" && current.enabled && current.change) current.change(delta);
    else setSunAngles(angles => ({ ...angles, [axis]: normalizeSunRotationAngle(angles[axis] + delta) }));
  };
  const move = (event: GestureResponderEvent) => {
    if (!mounted.current || !gestureActive.current) return;
    const next = eventTouches(event), previous = touches.current; touches.current = next;
    setView(current => orbitRoofGesture(current, previous, next));
  };
  const clear = () => { touches.current = []; gestureActive.current = false; };
  const rebase = (event: GestureResponderEvent) => { if (mounted.current) { touches.current = eventTouches(event); gestureActive.current = touches.current.length > 0; } };
  const gestureHint = "Drag to change view · Pinch to zoom";
  const yaw = camera.yaw * Math.PI / 180, elevation = camera.elevation * Math.PI / 180;
  const viewDirection = { x: Math.sin(yaw) * Math.cos(elevation), y: Math.cos(yaw) * Math.cos(elevation), z: Math.sin(elevation) };
  const visible = mesh.faces.filter(face => face.kind === "vertical" || face.normal.x * viewDirection.x + face.normal.y * viewDirection.y + face.normal.z * viewDirection.z > 1e-8)
    .sort((a, b) => projectRoofPoint(average(a.points), camera).z - projectRoofPoint(average(b.points), camera).z);
  const horizon = Array.from({ length: 73 }, (_, i) => sunPoint3({ azimuth: i * 5, elevation: 0 }));
  const skyPoint = (point: Point3) => projectSunFramePoint(point, camera, frontYaw, sunAngles);
  const skyPath = (points: Point3[], close = false) => sunFramePath3(points, camera, frontYaw, sunAngles, close);
  const button = (label: string, symbol: string, onPress: () => void, disabled = false, action?: string) => <Pressable key={label} testID={action} accessibilityRole="button" accessibilityLabel={t(label)} accessibilityState={{ disabled }} disabled={disabled} onPress={onPress} style={({ pressed }) => ({ minWidth: 44, minHeight: 44, maxWidth: "100%", paddingHorizontal: 12, paddingVertical: 8, borderRadius: 14, alignItems: "center", justifyContent: "center", borderWidth: 1, borderColor: colors.line, backgroundColor: pressed ? colors.fill : colors.surface, opacity: disabled ? .4 : 1 })}><Text style={{ fontSize: symbol.length === 1 ? 22 : 13, fontWeight: "600", flexShrink: 1, textAlign: "center" }}>{symbol}</Text></Pressable>;
  return <View testID="roof-sun-diagram" style={{ gap: 14, borderRadius: 24, padding: 12, backgroundColor: colors.bg }}>
    <Text style={{ fontSize: 17, fontWeight: "600" }}>{t("Roof and sun")}</Text>
    {day ? <Text style={{ fontSize: 13, color: colors.ink2 }}>{t("Sun path · {0}", date)} · {site.timeZoneId}</Text> : null}
    <View testID="roof-sun-orbit" accessible accessibilityRole="adjustable" accessibilityLabel={description} accessibilityHint={t(gestureHint)} accessibilityValue={{ min: 70, max: 250, now: Math.round(camera.zoom * 100), text: `${Math.round(camera.zoom * 100)}%` }} accessibilityActions={[{ name: "increment", label: t("Zoom in") }, { name: "decrement", label: t("Zoom out") }, { name: "camera-left", label: t("Rotate left") }, { name: "camera-right", label: t("Rotate right") }, { name: "camera-up", label: t("Tilt view up") }, { name: "camera-down", label: t("Tilt view down") }]} onAccessibilityAction={event => {
      const action = event.nativeEvent.actionName;
      if (action === "increment" || action === "decrement") adjustZoom(action === "increment" ? .2 : -.2);
      else if (action === "camera-left" || action === "camera-right") adjustView({ yaw: action === "camera-left" ? -10 : 10 });
      else if (action === "camera-up" || action === "camera-down") adjustView({ elevation: action === "camera-up" ? 10 : -10 });
    }}
      onStartShouldSetResponder={() => true} onMoveShouldSetResponder={() => true} onResponderTerminationRequest={() => false} onResponderGrant={rebase} onResponderStart={rebase} onResponderMove={move} onResponderEnd={rebase} onResponderRelease={clear} onResponderTerminate={clear} style={{ borderRadius: 20, overflow: "hidden", backgroundColor: colors.surface }}>
      <Svg width="100%" height={320} viewBox="0 0 320 320" accessible={false}>
        <Path testID="roof-sun-horizon" d={skyPath(horizon, true)} fill={colors.bg} stroke={colors.line} strokeWidth={1.5} />
        <Path testID="roof-sun-axes" d={skyPath([{ x: -110, y: 0, z: 0 }, { x: 110, y: 0, z: 0 }]) + " " + skyPath([{ x: 0, y: -110, z: 0 }, { x: 0, y: 110, z: 0 }])} fill="none" stroke={colors.line} strokeDasharray="3 5" />
        {[{ label: "N", azimuth: 0 }, { label: "E", azimuth: 90 }, { label: "S", azimuth: 180 }, { label: "W", azimuth: 270 }].map(point => { const p = skyPoint(sunPoint3({ ...point, elevation: 0 }, 124)); return <SvgText key={point.label} testID={`roof-sun-cardinal-${point.label}`} x={p.x} y={p.y + 5} fill={colors.ink2} fontSize={14} fontWeight="700" textAnchor="middle">{point.label}</SvgText>; })}
        {day?.paths.map((path, index) => <G key={index}>
          <Path testID="roof-sun-path" d={skyPath(path.map(p => sunPoint3(p)))} stroke={colors.solar} strokeWidth={2.5} strokeLinecap="round" strokeLinejoin="round" fill="none" />
          {path.length > 4 ? [1 / 3, 2 / 3].map(fraction => { const position = Math.min(path.length - 2, Math.floor(path.length * fraction)), point = skyPoint(sunPoint3(path[position]!)), next = skyPoint(sunPoint3(path[position + 1]!));
            return <Path key={fraction} testID="roof-sun-direction" d="M-5 -4L0 0L-5 4" transform={`translate(${point.x} ${point.y}) rotate(${Math.atan2(next.y - point.y, next.x - point.x) * 180 / Math.PI})`} stroke={colors.solar} strokeWidth={2.5} strokeLinecap="round" strokeLinejoin="round" fill="none" />;
          }) : null}
        </G>)}
        {mesh.faces.length ? <Path d={roofPath3(mesh.footprint.map(p => ({ ...p, x: p.x * 1.07, y: p.y * 1.07 })), camera, true)} fill={colors.ink} opacity={.09} /> : null}
        {visible.map(face => {
          const light = Math.abs(face.normal.x * .4 + face.normal.y * -.5 + face.normal.z * .8);
          return <G key={face.id} testID={face.roofNumber ? `roof-sun-roof-${face.roofNumber}` : face.id}>
            <Path testID={`roof-sun-face-${face.id}`} d={roofPath3(face.points, camera, true)} fill={face.kind === "wall" ? "#e1ded5" : face.roofNumber === 2 ? "#91604d" : "#a8725b"} stroke="#6b4c3d" strokeWidth={.7} strokeLinejoin="round" />
            <Path d={roofPath3(face.points, camera, true)} fill="#12120f" opacity={face.kind === "wall" ? .04 + .14 * (1 - light) : .08 + .2 * (1 - light)} />
            {face.kind === "wall" && Math.hypot(face.points[1]!.x - face.points[0]!.x, face.points[1]!.y - face.points[0]!.y) > 18 && Math.min(face.points[2]!.z, face.points[3]!.z) > 5 ? [.22, .62].map(u => {
              const left = between(face.points[0]!, face.points[1]!, u), right = between(face.points[0]!, face.points[1]!, u + .16), topLeft = between(face.points[3]!, face.points[2]!, u), topRight = between(face.points[3]!, face.points[2]!, u + .16);
              return <Path key={u} d={roofPath3([between(left, topLeft, .3), between(right, topRight, .3), between(right, topRight, .66), between(left, topLeft, .66)], camera, true)} fill="#456775" stroke="#89b2c3" strokeWidth={.6} />;
            }) : null}
            {face.panelTiles.map((tile, index) => <Path key={`panel-${index}`} testID={`roof-sun-panel-${face.roofNumber}-${index}`} d={roofPath3(tile, camera, true)} fill="#234356" stroke="#89b2c3" strokeWidth={.45} strokeLinejoin="round" />)}
            {face.grid.map((line, index) => <Path key={index} d={roofPath3(line, camera)} stroke="#89b2c3" strokeWidth={.6} opacity={.8} fill="none" />)}
          </G>;
        })}
        {mesh.ridge.map((line, index) => <Path key={index} testID="roof-sun-ridge" d={roofPath3(line, camera)} stroke={colors.ink3} strokeWidth={1.5} fill="none" />)}
        {visible.filter(face => face.roofNumber).map(face => {
          const corner = face.points.map(point => projectRoofPoint(point, camera)).reduce((lowest, point) => point.y > lowest.y ? point : lowest);
          return <G key={`label-${face.roofNumber}`} testID={`roof-sun-label-${face.roofNumber}`} transform={`translate(${corner.x} ${corner.y + 12})`}><Path d="M0 -12V-8" fill="none" stroke={colors.ink3} strokeWidth={.8} /><Circle r={8} fill={colors.sun} stroke={colors.onSun} strokeWidth={.8} /><SvgText y={3.5} fill={colors.onSun} textAnchor="middle" fontSize={10} fontWeight="700">{face.roofNumber}</SvgText></G>;
        })}
        {day?.now ? (() => { const point = skyPoint(sunPoint3(day.now)); return <G testID="roof-sun-current" transform={`translate(${point.x} ${point.y})`}><Circle r={10} fill={colors.sun} stroke={colors.onSun} strokeWidth={1.5} /><Path d="M0 -16V-13M0 13V16M-16 0H-13M13 0H16M-11 -11L-9 -9M9 9L11 11M-11 11L-9 9M9 -9L11 -11" fill="none" stroke={colors.solar} strokeWidth={2} strokeLinecap="round" /></G>; })() : null}
      </Svg>
    </View>
    <Text style={{ fontSize: 13, color: colors.ink2, textAlign: "center" }}>{t(gestureHint)}</Text>
    <Text style={{ fontSize: 13, lineHeight: 18, color: colors.ink3 }}>{t(settingDirection ? "X and Z adjust the diagram only. Y sets the roof azimuth." : "Sun rotation changes the diagram only. Solar calculations stay the same.")}</Text>
    {onOrientationChange ? <View style={{ alignItems: "center" }}>{button(settingDirection ? "Done" : "Set house direction", t(settingDirection ? "Done" : "Set house direction"), () => { if (!mounted.current || orientation.current.disabled) return; clear(); setOrientationMode(current => !current); }, directionDisabled)}</View> : null}
    <View style={{ flexDirection: "row", justifyContent: "center", flexWrap: "wrap", gap: 8 }}>
      {button("Zoom out", "−", () => adjustZoom(-.2), camera.zoom <= .7)}
      {button("Reset view", t("Reset view"), () => { if (mounted.current) { clear(); setView(initialView); } })}
      {button("Zoom in", "+", () => adjustZoom(.2), camera.zoom >= 2.5)}
    </View>
    <View style={{ gap: 8 }} accessibilityLabel={t("Sun rotation")}>
      <Text style={{ fontSize: 13, fontWeight: "600", textAlign: "center" }}>{t("Sun rotation")}</Text>
      <View style={{ flexDirection: "row", justifyContent: "center", flexWrap: "wrap", gap: 10 }}>
        {(["x", "y", "z"] as const).map(axis => <View key={axis} style={{ flexDirection: "row", gap: 4 }}>
          {button(axis === "x" ? "Rotate sun around X counterclockwise" : axis === "y" ? "Rotate sun around Y counterclockwise" : "Rotate sun around Z counterclockwise", `${axis.toUpperCase()}−`, () => rotateSun(axis, -10), directionDisabled, `sun-${axis}-minus`)}
          {button(axis === "x" ? "Rotate sun around X clockwise" : axis === "y" ? "Rotate sun around Y clockwise" : "Rotate sun around Z clockwise", `${axis.toUpperCase()}+`, () => rotateSun(axis, 10), directionDisabled, `sun-${axis}-plus`)}
        </View>)}
      </View>
    </View>
    {day ? <View style={{ flexDirection: "row", justifyContent: "center", flexWrap: "wrap", gap: 10 }}>
      {day.state === "polar-day" ? <Text style={{ fontSize: 13, color: colors.ink2 }}>{t("Sun stays above the horizon on this date.")}</Text> : day.state === "polar-night" ? <Text style={{ fontSize: 13, color: colors.ink2 }}>{t("Sun stays below the horizon on this date.")}</Text> : <><Text style={{ fontSize: 13, color: colors.ink2 }}>{t("Sunrise")} {time(day.sunrise)}</Text><Text style={{ fontSize: 13, color: colors.ink2 }}>{t("Sunset")} {time(day.sunset)}</Text></>}
      <Text style={{ fontSize: 13, color: colors.ink2 }}>{t(day.now ? "Sun now" : "Sun below horizon")}{day.now ? ` ${formatNumber(day.now.elevation, 0)}°` : ""}</Text>
    </View> : <Text accessibilityRole="alert" style={{ fontSize: 13, color: colors.ink2 }}>{t("Enter valid coordinates and a solar time zone to see the sun path.")}</Text>}
    <View style={{ flexDirection: "row", flexWrap: "wrap", gap: 8 }}>
      {roofs.map(roof => <View key={roof.number} style={{ flex: 1, minWidth: 110, borderRadius: 16, padding: 10, gap: 4, backgroundColor: colors.surface }}>
        <Text style={{ fontSize: 13, fontWeight: "600" }}>{t(roof.number === 1 ? "Roof 1" : "Roof 2")} · {formatNumber(roof.capacity, 2)} kWp</Text>
        <Text style={{ fontSize: 13, color: colors.ink2 }}>{t("Azimuth")} {formatNumber(roof.azimuth, 1)}° · {t("Tilt")} {formatNumber(roof.tilt, 1)}°</Text>
        {roof.panelCount != null ? <Text style={{ fontSize: 13, color: colors.ink2 }}>{t("{0} panels", roof.panelCount)}</Text> : null}
      </View>)}
    </View>
    {mesh.hasVerticalPanels ? <Text style={{ fontSize: 13, lineHeight: 18, color: colors.ink3 }}>{t("Vertical arrays are shown standing on the schematic roof.")}</Text> : null}
    {roofs.some(roof => roof.panelCount == null) ? <Text style={{ fontSize: 13, lineHeight: 18, color: colors.ink3 }}>{t("Enter the panel count for each roof to show its panels.")}</Text> : null}
    <Text style={{ fontSize: 13, lineHeight: 18, color: colors.ink3 }}>{t("Schematic roof; panel count and rows follow your settings. Dimensions and shadows are not modelled.")}</Text>
  </View>;
}
