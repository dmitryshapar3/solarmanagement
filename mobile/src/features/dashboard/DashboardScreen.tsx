import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Pressable, StyleSheet, Text, View } from "react-native";
import { NavigationProp, useFocusEffect, useNavigation } from "@react-navigation/native";
import {
  CirclePower,
  PlugZap,
  RefreshCcw
} from "lucide-react-native";
import {
  AppButton,
  Card,
  EmptyState,
  ErrorBanner,
  Header,
  LoadingState,
  ProgressBar,
  Screen,
  SectionTitle,
  StatusPill
} from "../../core/components";
import { Device, Rule } from "../../core/api/types";
import { batteryModeLabel, formatDateTime, formatPercent, formatTime, formatWatts, gridModeLabel, setDisplayTimeZone } from "../../core/format";
import { colors, spacing, typography } from "../../core/theme";
import { useAuth } from "../../application/AuthContext";

import { RootTabsParamList } from "../../application/navigationTypes";
import { GenerationPanel } from "../generation/GenerationScreen";
import { SalesPanel } from "../sales/SalesScreen";
import { useFocusedResource } from "../energy/useFocusedResource";
import { ManualOverrideCommand } from "./ManualOverrideCommand";

export function DashboardScreen() {
  const { api } = useAuth();
  const navigation = useNavigation<NavigationProp<RootTabsParamList>>();
  const resource = useFocusedResource("dashboard", useCallback((signal: AbortSignal, force: boolean) =>
    force ? api.refreshDashboard(signal) : api.getDashboard(signal), [api]));
  const dashboard = resource.data;
  const [selectedDeviceId, setSelectedDeviceId] = useState<string | null>(null);
  const [commandBusy, setCommandBusy] = useState<"on" | "off" | null>(null);
  const [commandError, setCommandError] = useState<string | null>(null);
  const command = useRef(new ManualOverrideCommand()).current;

  useEffect(() => {
    if (!dashboard) return;
    setDisplayTimeZone(dashboard.timeZoneId);
    setSelectedDeviceId((current) => pickDeviceId(dashboard.manualDevices, current));
  }, [dashboard]);

  useFocusEffect(useCallback(() => {
    command.activate();
    setCommandBusy(command.busy);
    return () => command.deactivate();
  }, [command]));

  const selectedDevice = useMemo(
    () => dashboard?.manualDevices.find((device) => device.id === selectedDeviceId) ?? null,
    [dashboard?.manualDevices, selectedDeviceId]
  );

  async function setSocketState(isOn: boolean) {
    if (!selectedDeviceId) return;
    await command.run(isOn ? "on" : "off", () => api.setDeviceState(selectedDeviceId, isOn), {
      busyChanged: (value) => {
        setCommandBusy(value);
        if (value !== null) setCommandError(null);
      },
      completed: async () => {
        // A pre-toggle read must not overwrite the state acknowledged by the command.
        resource.invalidate();
        await resource.refresh();
      },
      failed: (error) => setCommandError(error instanceof Error ? error.message : "Unable to change socket state.")
    });
  }

  if (resource.loading && !dashboard) {
    return <Screen scroll={false}><LoadingState label="Loading dashboard..." /></Screen>;
  }

  return (
    <Screen refreshing={resource.loading} onRefresh={() => void resource.refresh(true)}>
      <Header title="Dashboard" subtitle="Your energy at a glance" />
      <ErrorBanner message={commandError ?? resource.error} />
      <Card style={styles.statusCard}>
        <View style={styles.statusHeading}>
          <View style={styles.statusTitleCopy}>
            <Text style={styles.statusTitle}>Energy status</Text>
            <Text style={styles.metaText}>{dashboard?.inverter ? `DeyeCloud · last poll ${formatTime(dashboard.inverter.timestamp)}` : "Waiting for the first Deye reading"}</Text>
          </View>
          <AppButton label="Refresh" icon={RefreshCcw} onPress={() => void resource.refresh(true)} loading={resource.loading} variant="secondary" compact />
        </View>
        <View style={styles.statusMetrics}>
          <View style={styles.statusMetric}>
            <Text style={styles.metaText}>Battery SOC</Text>
            <Text style={[styles.statusValue, { color: colors.primary }]}>{formatPercent(dashboard?.inverter?.batterySoc)}</Text>
            {dashboard?.inverter ? <ProgressBar value={dashboard.inverter.batterySoc} color={socColor(dashboard.inverter.batterySoc)} /> : null}
          </View>
          <View style={[styles.statusMetric, styles.statusSeparated]}>
            <Text style={styles.metaText}>Battery power</Text>
            <Text style={styles.statusValue}>{dashboard?.inverter ? formatWatts(Math.abs(dashboard.inverter.batteryPower)) : "—"}</Text>
            <Text style={styles.metaText}>{dashboard?.inverter ? batteryModeLabel(dashboard.inverter.batteryPower) : "Awaiting reading"}</Text>
          </View>
          <View style={[styles.statusMetric, styles.statusSeparated]}>
            <Text style={styles.metaText}>Grid</Text>
            <Text style={styles.statusValue}>{dashboard?.inverter ? formatWatts(Math.abs(dashboard.inverter.gridConsumption)) : "—"}</Text>
            <Text style={styles.metaText}>{dashboard?.inverter ? gridModeLabel(dashboard.inverter.gridConsumption) : "Awaiting reading"}</Text>
          </View>
        </View>
      </Card>
      <GenerationPanel compact onDetails={() => navigation.navigate("Generation")} />
      <SalesPanel compact onDetails={() => navigation.navigate("Sales")} />

      <SectionTitle title="Manual Override" />
      <Card style={styles.quickActions}>
        {dashboard?.manualDevices.length ? (
          <>
            <View style={styles.devicePicker}>
              {dashboard.manualDevices.map((device) => (
                <DeviceChoice
                  key={device.id}
                  device={device}
                  selected={device.id === selectedDeviceId}
                  onPress={() => setSelectedDeviceId(device.id)}
                />
              ))}
            </View>
            <View style={styles.actionRow}>
              <AppButton
                label="Socket ON"
                icon={CirclePower}
                onPress={() => void setSocketState(true)}
                loading={commandBusy === "on"}
                disabled={!selectedDevice || commandBusy !== null}
              />
              <AppButton
                label="Socket OFF"
                icon={CirclePower}
                onPress={() => void setSocketState(false)}
                loading={commandBusy === "off"}
                disabled={!selectedDevice || commandBusy !== null}
                variant="danger"
              />
            </View>
          </>
        ) : (
          <EmptyState title="No sockets configured." detail="Configure the Shelly socket backend in Settings or add a rule with a target device." />
        )}
      </Card>

      <SectionTitle title="Rules" />
      {dashboard?.rules.length ? (
        <View style={styles.list}>
          {dashboard.rules.map((rule) => (
            <RuleLine key={rule.id} rule={rule} />
          ))}
        </View>
      ) : (
        <EmptyState title="No rules configured." />
      )}

      <SectionTitle title="Smart Sockets" trailing={<Text style={styles.metaText}>{formatDateTime(dashboard?.deviceLastUpdated)}</Text>} />
      {dashboard?.devices.length ? (
        <View style={styles.list}>
          {dashboard.devices.map((device) => (
            <DeviceLine key={device.id} device={device} />
          ))}
        </View>
      ) : (
        <EmptyState title="No devices found." />
      )}
    </Screen>
  );
}

function DeviceChoice({ device, selected, onPress }: { device: Device; selected: boolean; onPress: () => void }) {
  return (
    <Pressable onPress={onPress} style={[styles.deviceChoice, selected && styles.deviceChoiceSelected]}>
      <View style={styles.deviceChoiceTitle}>
        <PlugZap color={device.online ? colors.primary : colors.subtle} size={16} />
        <Text style={styles.deviceChoiceName} numberOfLines={1}>{device.name}</Text>
      </View>
      <StatusPill label={!device.online ? "Offline" : device.isOn ? "ON" : "OFF"} tone={!device.online ? "neutral" : device.isOn ? "success" : "warning"} />
    </Pressable>
  );
}

function RuleLine({ rule }: { rule: Rule }) {
  return (
    <Card style={styles.rowCard}>
      <View style={styles.rowCopy}>
        <Text style={styles.rowTitle} numberOfLines={1}>{rule.name}</Text>
        <Text style={styles.rowSubtitle}>{`SOC >= ${rule.socTurnOnThreshold}% | every ${rule.intervalSeconds}s`}</Text>
        <Text style={styles.rowSubtitle}>{`Switched ${formatDateTime(rule.currentStateChangedAt)} | Checked ${formatDateTime(rule.lastEvaluated)}`}</Text>
      </View>
      <View style={styles.rowStatus}>
        <StatusPill label={rule.currentState ? "ON" : "OFF"} tone={rule.currentState ? "success" : "neutral"} />
        {!rule.enabled ? <StatusPill label="Disabled" /> : null}
      </View>
    </Card>
  );
}

function DeviceLine({ device }: { device: Device }) {
  return (
    <Card style={styles.rowCard}>
      <View style={styles.rowCopy}>
        <Text style={styles.rowTitle} numberOfLines={1}>{device.name}</Text>
        <Text style={styles.rowSubtitle}>{device.category ?? "Socket"}</Text>
      </View>
      <View style={styles.rowStatus}>
        <Text style={styles.powerText}>{formatWatts(device.currentPowerW)}</Text>
        <StatusPill label={!device.online ? "Offline" : device.isOn ? "ON" : "OFF"} tone={!device.online ? "neutral" : device.isOn ? "success" : "warning"} />
      </View>
    </Card>
  );
}

function pickDeviceId(devices: Device[], current: string | null): string | null {
  if (current && devices.some((device) => device.id === current)) {
    return current;
  }

  return (
    devices.find((device) => device.online && device.isOn)?.id ??
    devices.find((device) => device.online)?.id ??
    devices[0]?.id ??
    null
  );
}

function socColor(soc: number): string {
  if (soc >= 80) {
    return colors.primary;
  }

  if (soc >= 40) {
    return colors.amber;
  }

  return colors.red;
}

const styles = StyleSheet.create({
  statusCard: { gap: spacing.lg },
  statusHeading: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: spacing.sm },
  statusTitleCopy: { flex: 1, gap: 4 },
  statusTitle: { color: colors.text, fontSize: 18, fontWeight: "700" },
  statusMetrics: { flexDirection: "row", gap: spacing.sm },
  statusMetric: { flex: 1, gap: spacing.sm, minWidth: 0 },
  statusSeparated: { borderLeftWidth: StyleSheet.hairlineWidth, borderLeftColor: colors.border, paddingLeft: spacing.sm },
  statusValue: { color: colors.text, fontSize: 20, fontWeight: "800", fontVariant: ["tabular-nums"] },
  metaText: { color: colors.muted, fontSize: typography.caption },
  quickActions: {
    gap: spacing.lg
  },
  devicePicker: {
    gap: spacing.sm
  },
  deviceChoice: {
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: colors.border,
    borderRadius: 8,
    backgroundColor: colors.surfaceRaised,
    padding: spacing.md,
    gap: spacing.sm
  },
  deviceChoiceSelected: {
    borderColor: colors.primary
  },
  deviceChoiceTitle: {
    flexDirection: "row",
    alignItems: "center",
    gap: spacing.sm
  },
  deviceChoiceName: {
    flex: 1,
    color: colors.text,
    fontSize: typography.body,
    fontWeight: "700"
  },
  actionRow: {
    flexDirection: "row",
    gap: spacing.md
  },
  list: {
    gap: spacing.sm
  },
  rowCard: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: spacing.md,
    paddingVertical: spacing.md
  },
  rowCopy: {
    flex: 1,
    gap: spacing.xs
  },
  rowTitle: {
    color: colors.text,
    fontSize: typography.body,
    fontWeight: "700"
  },
  rowSubtitle: {
    color: colors.muted,
    fontSize: typography.caption
  },
  rowStatus: {
    alignItems: "flex-end",
    gap: spacing.xs
  },
  powerText: {
    color: colors.text,
    fontSize: typography.caption,
    fontWeight: "700"
  }
});
