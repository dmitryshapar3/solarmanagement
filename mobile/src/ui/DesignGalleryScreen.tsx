import { useState } from "react";
import { Image, View } from "react-native";
import { ChevronRight, Plus, Power, Sun } from "lucide-react-native";
import { AppButton, Banner, Card, DataRow, EmptyState, ErrorBanner, Header, IconButton, LoadingState, MetricTile, NativeSwitch, NavigationRow, ProgressBar, Screen, SectionTitle, SegmentedControl, StatusPill, SwitchRow, TextField, ThemedText as Text } from "../core/components";
import { useTheme } from "./theme/ThemeProvider";
import { SelectField } from "./forms/SelectField";
import { ThresholdRange } from "./forms/ThresholdRange";
import { ProductionChart } from "./charts/ProductionChart";
import { ExportChart } from "./charts/ExportChart";
import { EnergyFlow } from "./energy/EnergyFlow";
import type { ChartPoint } from "../features/energy/chartPolicy";
import { demoInverter } from "../features/demo/fixtures";

const points: ChartPoint[] = Array.from({ length: 12 }, (_, hour) => ({ timestamp: new Date(Date.UTC(2026, 0, 1, hour + 6)).toISOString(), label: String(hour + 6), actual: hour === 5 ? null : Math.max(0, Math.sin(hour / 11 * Math.PI) * 4.8), possible: { lowerKw: Math.max(0, Math.sin(hour / 11 * Math.PI) * 3.5), upperKw: Math.max(0, Math.sin(hour / 11 * Math.PI) * 5.5) }, description: `Sample interval ${hour + 6}` }));
export function DesignGalleryScreen() {
  const { colors, appearance, setAppearance } = useTheme(); const [selected, setSelected] = useState("production"); const [on, setOn] = useState(true); const [choice, setChoice] = useState("one"); const [range, setRange] = useState({ off: 55, on: 75 });
  return <Screen><Header title="Design gallery" subtitle="Development only · sample data" />
    <SegmentedControl value={appearance} onChange={value => void setAppearance(value)} options={[{ value: "light", label: "Light" }, { value: "dark", label: "Dark" }, { value: "system", label: "System" }]} />
    <Card><View style={{ flexDirection: "row", alignItems: "center", gap: 10 }}><Image source={require("../../assets/smartsolar-mark.png")} style={{ width: 40, height: 40 }} /><Text style={{ fontFamily: "Unbounded-Bold", fontSize: 24 }}>SmartSolar</Text></View></Card>
    <SectionTitle title="Typography" /><Card style={{ gap: 12 }}>{[{ size: 72, text: "3.38", weight: "700" }, { size: 52, text: "128.4", weight: "700" }, { size: 34, text: "Page title", weight: "700" }, { size: 19, text: "Section title", weight: "600" }, { size: 17, text: "Body · Солнечная энергия", weight: "400" }, { size: 15, text: "Secondary text", weight: "500" }, { size: 13, text: "Helper text and units", weight: "500" }, { size: 11, text: "Navigation caption", weight: "600" }].map(sample => <Text key={sample.size} style={{ fontSize: sample.size, lineHeight: sample.size * 1.16, fontWeight: sample.weight as "700", fontVariant: ["tabular-nums"] }}>{sample.text}</Text>)}</Card>
    <SectionTitle title="Controls" /><AppButton label="Primary action" icon={Plus} onPress={() => {}} /><AppButton label="Secondary action" variant="secondary" onPress={() => {}} /><AppButton label="Quiet action" variant="quiet" onPress={() => {}} /><AppButton label="Destructive action" variant="critical" onPress={() => {}} /><AppButton label="Disabled action" disabled onPress={() => {}} /><AppButton label="Loading action" loading onPress={() => {}} /><View style={{ flexDirection: "row", gap: 12 }}><IconButton icon={ChevronRight} accessibilityLabel="Sample glass button" onPress={() => {}} /><IconButton icon={Power} accessibilityLabel="Sample disabled button" disabled onPress={() => {}} /></View>
    <SegmentedControl value={selected} onChange={setSelected} options={[{ value: "production", label: "Production" }, { value: "export", label: "Export" }]} />
    <Card><SwitchRow title="Enabled" subtitle="Native switch in the current appearance" value={on} onValueChange={setOn} /><NativeSwitch label="Disabled switch" value={false} disabled onValueChange={() => {}} /></Card>
    <TextField label="Label" value="Warsaw rooftop" onChangeText={() => {}} helper="A helpful sentence below the field." /><TextField label="Error field" value="invalid" onChangeText={() => {}} error="Enter a valid value." /><TextField label="Disabled field" value="Read only" editable={false} onChangeText={() => {}} unit="kW" />
    <SelectField label="Select a value" value={choice} onChange={setChoice} options={[{ value: "one", label: "First option" }, { value: "two", label: "Second option" }, { value: "three", label: "Unavailable option", disabled: true }]} />
    <Card><ThresholdRange off={range.off} on={range.on} onChange={(which, value) => setRange(current => ({ ...current, [which]: value }))} /><ProgressBar value={66} /></Card>
    <SectionTitle title="Statuses" /><View style={{ flexDirection: "row", flexWrap: "wrap", gap: 8 }}><StatusPill label="Measured" tone="success" /><StatusPill label="Partial" tone="warning" /><StatusPill label="Unavailable" tone="neutral" /><StatusPill label="Sending" tone="info" /><StatusPill label="Rejected" tone="danger" /></View><Banner tone="sample">Sample data disclosure</Banner><Banner tone="warning">Missing readings stay gaps.</Banner><ErrorBanner message="The latest data could not be loaded." /><EmptyState title="Nothing here yet" detail="The next step belongs here." /><LoadingState />
    <SectionTitle title="Rows and metrics" /><MetricTile label="Produced today" value="18.4 kWh" icon={Sun} color={colors.solar} /><Card><DataRow label="Last reading" value="13:12" detail="Measured value" /><NavigationRow title="Open settings" value="Light" onPress={() => {}} /></Card>
    <SectionTitle title="Charts and flow" /><Card><ProductionChart points={points} now={points[6]!.timestamp} /></Card><Card><ProductionChart points={points} bars unit="kWh" /></Card><Card><ExportChart points={points.map(point => ({ ...point, completed: point.actual == null ? null : point.actual * .24 }))} unit="PLN" /></Card><Card><EnergyFlow inverter={demoInverter(new Date(), "Europe/Warsaw")} /></Card><Card><EnergyFlow inverter={null} /></Card>
  </Screen>;
}
