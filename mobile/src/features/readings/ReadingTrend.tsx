import Svg, { Line, Path, Text as SvgText } from "react-native-svg";
import type { ReadingDetailsRow } from "../../core/api/redesignTypes";
import { useTheme } from "../../ui/theme/ThemeProvider";
import { chartGeometry, chartHeight, chartWidth, type ChartPoint } from "../energy/chartPolicy";
import { formatTime } from "../../core/format";

export function readingPoints(rows: ReadingDetailsRow[], metric: "batterySoc" | "solarProduction" | "gridConsumption"): ChartPoint[] {
  const result: ChartPoint[] = []; let previous: ReadingDetailsRow | undefined;
  for (const row of [...rows].sort((a, b) => Date.parse(a.timestamp) - Date.parse(b.timestamp))) {
    if (previous && (row.inverterId !== previous.inverterId || row.configurationRevision !== previous.configurationRevision || row.runtimeGeneration !== previous.runtimeGeneration || Date.parse(row.timestamp) - Date.parse(previous.timestamp) > 10 * 60000)) result.push({ timestamp: new Date(Date.parse(row.timestamp) - 1).toISOString(), label: "", description: "", actual: null });
    result.push({ timestamp: row.timestamp, label: formatTime(row.timestamp), description: "", actual: row[metric] === null ? null : row[metric]! / (metric === "batterySoc" ? 1 : 1000) }); previous = row;
  }
  return result;
}
export function ReadingTrend({ rows, metric = "batterySoc" }: { rows: ReadingDetailsRow[]; metric?: "batterySoc" | "solarProduction" | "gridConsumption" }) {
  const { colors } = useTheme(); const points = readingPoints(rows, metric); const geometry = chartGeometry(points, "generation", metric === "batterySoc" ? { minimum: 0, maximum: 100 } : undefined);
  return <Svg width="100%" height={90} viewBox={`0 0 ${chartWidth} ${chartHeight}`}><Line x1={38} x2={348} y1={176} y2={176} stroke={colors.line} />{geometry.actualPaths.map((path, index) => <Path key={index} d={path} fill="none" stroke={metric === "batterySoc" ? colors.battery : metric === "solarProduction" ? colors.solar : colors.grid} strokeWidth={3} strokeLinecap="round" strokeLinejoin="round" />)}{[0, Math.max(0, points.length - 1)].map((index, i) => <SvgText key={i} x={geometry.x(index)} y={205} fill={colors.ink3} fontSize={13} fontFamily="Onest-Medium" textAnchor={i ? "end" : "start"}>{points[index]?.label}</SvgText>)}</Svg>;
}
