import { View } from "react-native";
import { useLanguage } from "../../application/LanguageContext";
import type { ReadingDetailsRow } from "../../core/api/redesignTypes";
import { ThemedText as Text } from "../../core/components";
import { formatNumber, formatSignedWatts } from "../../core/format";
import { useTheme } from "../../ui/theme/ThemeProvider";
export function ReadingMetrics({ row }: { row: ReadingDetailsRow }) {
  const { colors } = useTheme(); const { t } = useLanguage();
  return <View style={{ flexDirection: "row", flexWrap: "wrap", gap: 12 }}>{[
    { label: "SOC", value: row.batterySoc === null ? "—" : `${formatNumber(row.batterySoc)}%` }, { label: "Solar", value: row.solarProduction === null ? "—" : formatSignedWatts(row.solarProduction) },
    { label: "Grid", value: row.gridConsumption === null ? "—" : formatSignedWatts(row.gridConsumption) }, { label: "Battery", value: row.batteryPower === null ? "—" : formatSignedWatts(row.batteryPower) },
    { label: "Load", value: row.loadPower === null ? "—" : formatSignedWatts(row.loadPower) }, { label: "Voltage", value: row.batteryVoltage === null ? "—" : `${formatNumber(row.batteryVoltage, 1)} V` },
    { label: "Temperature", value: row.batteryTemperature === null ? "—" : `${formatNumber(row.batteryTemperature, 1)} °C` }, { label: "Current", value: row.batteryCurrent === null ? "—" : `${formatNumber(row.batteryCurrent, 1)} A` }
  ].map(metric => <View key={metric.label} style={{ minWidth: 80, gap: 3 }}><Text style={{ fontSize: 13, color: colors.ink2 }}>{t(metric.label)}</Text><Text style={{ fontSize: 15, fontWeight: "600" }}>{metric.value}</Text></View>)}</View>;
}
