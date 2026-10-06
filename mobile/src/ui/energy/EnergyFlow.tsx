import { View } from "react-native";
import Svg, { Circle, G, Line, Path, Rect, Text as SvgText } from "react-native-svg";
import { useLanguage } from "../../application/LanguageContext";
import type { InverterData, Rule } from "../../core/api/types";
import { ThemedText as Text } from "../../core/components";
import { batteryModeLabel, formatPercent, formatWatts, gridModeLabel } from "../../core/format";
import { useTheme } from "../theme/ThemeProvider";

export function EnergyFlow({ inverter, thresholds = [] }: { inverter: InverterData | null; thresholds?: Rule[] }) {
  const { colors } = useTheme(); const { t } = useLanguage();
  const value = (kind: "solar" | "grid" | "battery" | "load") => {
    if (!inverter) return null;
    const pairs = { solar: [inverter.solarPowerValid, inverter.solarProduction], grid: [inverter.gridPowerValid, inverter.gridConsumption], battery: [inverter.batteryPowerValid, inverter.batteryPower], load: [inverter.loadPowerValid, inverter.loadPower] } as const;
    const [valid, watts] = pairs[kind]; return valid && Number.isFinite(watts) ? watts : null;
  };
  const solar = value("solar"), grid = value("grid"), battery = value("battery"), load = value("load");
  const soc = inverter?.batterySocValid ? inverter.batterySoc : null;
  const font = { fontFamily: "Onest-Medium", fontSize: 13, fill: colors.ink2 };
  const amount = (watts: number | null) => watts === null ? "—" : formatWatts(Math.abs(watts));
  const labels = [{ label: "Solar", value: solar, x: 118, y: 30, anchor: "end" }, { label: "Grid", value: grid, x: 310, y: 45, anchor: "end" }, { label: "Battery", value: battery, x: 4, y: 175, anchor: "start" }, { label: "Home", value: load, x: 208, y: 210, anchor: "start" }] as const;
  const line = (watts: number | null, color: string, x1: number, y1: number, x2: number, y2: number, reverse = false) => {
    const width = watts === null ? 1 : Math.min(6, 1.5 + Math.abs(watts) / 1000);
    const active = watts !== null && Math.abs(watts) > 0;
    const cx = (x1 + x2) / 2, cy = (y1 + y2) / 2;
    const angle = Math.atan2(y2 - y1, x2 - x1) * 180 / Math.PI + (reverse ? 180 : 0);
    return <G><Line x1={x1} y1={y1} x2={x2} y2={y2} stroke={active ? color : colors.line} strokeOpacity={active ? .3 : 1} strokeWidth={width} strokeLinecap="round" />{active ? <Path d="M-6 -5 L0 0 L-6 5" transform={`translate(${cx} ${cy}) rotate(${angle})`} stroke={color} fill="none" strokeWidth={2.5} strokeLinecap="round" strokeLinejoin="round" /> : null}</G>;
  };
  return <View style={{ gap: 12 }}><Svg width="100%" height={248} viewBox="0 0 318 248" accessibilityLabel={t("Energy flow from the latest reported measurements")}>
    {line(solar, colors.solar, 159, 60, 159, 124)}{line(battery, colors.battery, 58, 124, 151, 124, battery !== null && battery < 0)}{line(grid, colors.grid, 167, 124, 260, 124, grid !== null && grid > 0)}{line(load, colors.ink, 159, 132, 159, 190)}<Circle cx={159} cy={124} r={6} fill={colors.surface} stroke={colors.ink3} strokeWidth={2} />
    <G transform="translate(131 2)"><Circle cx={28} cy={28} r={28} fill={colors.sun} /><Circle cx={28} cy={28} r={6} stroke={colors.onSun} fill="none" strokeWidth={2} />{Array.from({ length: 8 }, (_, i) => <Line key={i} x1={28} x2={28} y1={13} y2={17} stroke={colors.onSun} strokeWidth={2} transform={`rotate(${i * 45} 28 28)`} />)}</G>
    <G transform="translate(262 96)"><Circle cx={28} cy={28} r={28} fill={colors.grid} /><Path d="M28 15v26M16 20h24M18 20l10 10 10-10" stroke="#FFFFFF" fill="none" strokeWidth={2} /></G>
    <G transform="translate(0 96)"><Circle cx={28} cy={28} r={28} fill={colors.battery} /><Rect x={14} y={20} width={24} height={16} rx={3} stroke="#FFFFFF" fill="none" strokeWidth={2} /><Line x1={41} x2={41} y1={25} y2={31} stroke="#FFFFFF" strokeWidth={2} /><Rect x={17} y={23} width={soc === null ? 0 : Math.max(0, Math.min(18, soc / 100 * 18))} height={10} rx={1} fill="#FFFFFF" /></G>
    <G transform="translate(131 192)"><Circle cx={28} cy={28} r={28} fill={colors.home} /><Path d="M15 26l13-11 13 11v15H15ZM24 41V29h8v12" stroke={colors.bg} fill="none" strokeWidth={2} strokeLinejoin="round" /></G>
    {labels.map(label => <G key={label.label}><SvgText {...font} x={label.x} y={label.y} textAnchor={label.anchor}>{t(label.label)}</SvgText><SvgText x={label.x} y={label.y + 24} textAnchor={label.anchor} fill={colors.ink} fontSize={19} fontFamily="Onest-Bold">{amount(label.value)}</SvgText></G>)}
  </Svg><View style={{ flexDirection: "row", justifyContent: "space-between", gap: 12 }}><Text style={{ color: colors.ink2, fontSize: 13 }}>{t("Battery")} · {soc === null ? "—" : formatPercent(soc)} · {battery === null ? t("Awaiting reading") : batteryModeLabel(battery)}</Text><Text style={{ color: colors.ink2, fontSize: 13 }}>{t("Grid")} · {grid === null ? t("Awaiting reading") : gridModeLabel(grid)}</Text></View>
    {soc !== null ? <View style={{ gap: 8 }}><View style={{ height: 7, backgroundColor: colors.fill, borderRadius: 4 }}><View style={{ height: 7, width: `${Math.max(0, Math.min(100, soc))}%`, backgroundColor: colors.battery, borderRadius: 4 }} />{thresholds.flatMap(rule => [{ value: rule.socTurnOnThreshold, key: `on${rule.id}` }, { value: rule.useSeparateSocTurnOffThreshold ? rule.socTurnOffThreshold : rule.socTurnOnThreshold, key: `off${rule.id}` }]).map(mark => <View key={mark.key} style={{ position: "absolute", left: `${mark.value}%`, top: -3, width: 2, height: 13, backgroundColor: colors.ink }} />)}</View>{thresholds.length ? <Text style={{ fontSize: 13, color: colors.ink3 }}>{thresholds.map(rule => t("{0}: on {1}% · off {2}%", rule.name, rule.socTurnOnThreshold, rule.useSeparateSocTurnOffThreshold ? rule.socTurnOffThreshold : rule.socTurnOnThreshold)).join(" · ")}</Text> : null}</View> : null}
  </View>;
}
