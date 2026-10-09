import { Animated, View } from "react-native";
import Svg, { Circle, G, Line, Path, Rect, Text as SvgText } from "react-native-svg";
import { useLanguage } from "../../application/LanguageContext";
import type { InverterData, Rule } from "../../core/api/types";
import { ThemedText as Text } from "../../core/components";
import { batteryModeLabel, formatPercent, formatWatts, gridModeLabel } from "../../core/format";
import { useTheme } from "../theme/ThemeProvider";
import { energyFlowDirection, energyFlowReadings, type FlowKind } from "./energyFlowPolicy";
import { useEnergyFlowAnimation } from "./useEnergyFlowAnimation";

const AnimatedGroup = Animated.createAnimatedComponent(G);
const chevron = "M-5 -4 L0 0 L-5 4 M-12 -4 L-7 0 L-12 4";

export function EnergyFlow({ inverter, thresholds = [] }: { inverter: InverterData | null; thresholds?: Rule[] }) {
  const { colors } = useTheme();
  const { t } = useLanguage();
  const readings = energyFlowReadings(inverter);
  const { grid, battery, soc } = readings;
  const hasFlow = (["solar", "grid", "battery", "load"] as const).some(kind => energyFlowDirection(kind, readings[kind]) !== null);
  const { progress, moving } = useEnergyFlowAnimation(hasFlow);
  const amount = (watts: number | null) => watts === null ? "—" : formatWatts(Math.abs(watts));
  const labels = [
    { kind: "solar", label: "Solar", x: 180, y: 14 },
    { kind: "battery", label: "Battery", x: 65, y: 192 },
    { kind: "grid", label: "Grid", x: 295, y: 192 },
    { kind: "load", label: "Load", x: 180, y: 294 }
  ] as const;
  const summary = labels.map(({ kind, label }) => `${t(label)}: ${amount(readings[kind])}`).join(" · ");

  // Coordinates follow the actual energy direction, so static and moving arrows
  // keep the same meaning for both positive and negative signed measurements.
  const track = (kind: FlowKind, color: string, source: [number, number], destination: [number, number], sourceIsJunction: boolean) => {
    const watts = readings[kind];
    const direction = energyFlowDirection(kind, watts);
    const reverse = direction === (sourceIsJunction ? "in" : "out");
    const [start, end] = reverse ? [destination, source] : [source, destination];
    const length = Math.hypot(end[0] - start[0], end[1] - start[1]);
    const rotation = Math.atan2(end[1] - start[1], end[0] - start[0]) * 180 / Math.PI;
    const active = direction !== null;
    const strokeWidth = active ? Math.min(5, 2.5 + Math.abs(watts!) / 2500) : 2;
    return <G key={kind} testID={`energy-flow-${kind}-track`}>
      <Line x1={start[0]} y1={start[1]} x2={end[0]} y2={end[1]} stroke={active ? color : colors.line} strokeOpacity={active ? .24 : 1} strokeWidth={strokeWidth} strokeLinecap="round" />
      {active ? <G transform={`translate(${start[0]} ${start[1]}) rotate(${rotation})`}>
        {moving ? <AnimatedGroup testID={`energy-flow-${kind}-moving`} transform={[{ translateX: progress.interpolate({ inputRange: [0, 1], outputRange: [14, length - 5] }) }]} opacity={progress.interpolate({ inputRange: [0, .12, .88, 1], outputRange: [0, 1, 1, 0] })}>
          <Path d={chevron} stroke={color} fill="none" strokeWidth={2.5} strokeLinecap="round" strokeLinejoin="round" />
        </AnimatedGroup> : <Path testID={`energy-flow-${kind}-static`} d={chevron} transform={`translate(${(length + 12) / 2} 0)`} stroke={color} fill="none" strokeWidth={2.5} strokeLinecap="round" strokeLinejoin="round" />}
      </G> : null}
    </G>;
  };

  return <View style={{ gap: 14 }}>
    <Svg width="100%" height={310} viewBox="0 0 360 330" accessible accessibilityRole="image" accessibilityLabel={`${t("Energy flow from the latest reported measurements")}. ${summary}`}>
      {track("solar", colors.solar, [180, 97], [180, 137], false)}
      {track("battery", colors.battery, [96, 148], [169, 148], false)}
      {track("grid", colors.grid, [191, 148], [264, 148], true)}
      {track("load", colors.home, [180, 159], [180, 218], true)}
      <Circle cx={180} cy={148} r={10} fill={colors.fill} />
      <Circle cx={180} cy={148} r={4} fill={colors.ink3} />

      <G transform="translate(155 41)"><Circle cx={25} cy={25} r={25} fill={colors.sun} /><Circle cx={25} cy={25} r={6} stroke={colors.onSun} fill="none" strokeWidth={2} />{Array.from({ length: 8 }, (_, index) => <Line key={index} x1={25} x2={25} y1={10} y2={14} stroke={colors.onSun} strokeWidth={2} strokeLinecap="round" transform={`rotate(${index * 45} 25 25)`} />)}</G>
      <G transform="translate(270 123)"><Circle cx={25} cy={25} r={25} fill={colors.grid} /><Path d="M25 12v26M13 17h24M15 17l10 10 10-10M17 38l8-26 8 26" stroke={colors.switchKnob} fill="none" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" /></G>
      <G transform="translate(40 123)"><Circle cx={25} cy={25} r={25} fill={colors.battery} /><Rect x={12} y={17} width={24} height={16} rx={3} stroke={colors.switchKnob} fill="none" strokeWidth={2} /><Line x1={39} x2={39} y1={22} y2={28} stroke={colors.switchKnob} strokeWidth={2} strokeLinecap="round" /><Rect x={15} y={20} width={soc === null ? 0 : soc / 100 * 18} height={10} rx={1} fill={colors.switchKnob} /></G>
      <G transform="translate(155 224)"><Circle cx={25} cy={25} r={25} fill={colors.home} /><Path d="M12 23l13-11 13 11v15H12ZM21 38V26h8v12" stroke={colors.bg} fill="none" strokeWidth={2} strokeLinejoin="round" /></G>

      {labels.map(({ kind, label, x, y }) => <G key={kind}>
        <SvgText x={x} y={y} textAnchor="middle" fontFamily="Onest-Medium" fontSize={13} fill={colors.ink2}>{t(label)}</SvgText>
        <SvgText testID={`energy-flow-${kind}-value`} x={x} y={y + 23} textAnchor="middle" fill={colors.ink} fontSize={20} fontFamily="Onest-Bold">{amount(readings[kind])}</SvgText>
      </G>)}
    </Svg>
    <View style={{ flexDirection: "row", flexWrap: "wrap", gap: 8 }}>
      <View style={{ flexBasis: 140, flexGrow: 1, backgroundColor: colors.fill, borderRadius: 12, paddingHorizontal: 12, paddingVertical: 10 }}><Text style={{ color: colors.ink2, fontSize: 13 }}>{t("Battery")} · {soc === null ? "—" : formatPercent(soc)}</Text><Text style={{ color: colors.ink, fontSize: 13 }}>{battery === null ? t("Awaiting reading") : batteryModeLabel(battery)}</Text></View>
      <View style={{ flexBasis: 140, flexGrow: 1, backgroundColor: colors.fill, borderRadius: 12, paddingHorizontal: 12, paddingVertical: 10 }}><Text style={{ color: colors.ink2, fontSize: 13 }}>{t("Grid")}</Text><Text style={{ color: colors.ink, fontSize: 13 }}>{grid === null ? t("Awaiting reading") : gridModeLabel(grid)}</Text></View>
    </View>
    {thresholds.length ? <Text style={{ fontSize: 13, color: colors.ink3 }}>{thresholds.map(rule => t("{0}: on {1}% · off {2}%", rule.name, rule.socTurnOnThreshold, rule.useSeparateSocTurnOffThreshold ? rule.socTurnOffThreshold : rule.socTurnOnThreshold)).join(" · ")}</Text> : null}
  </View>;
}
