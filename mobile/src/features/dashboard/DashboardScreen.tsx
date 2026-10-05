import { useDemoDisplayName } from "../demo/useDemoDisplayName";
import { useLanguage } from "../../application/LanguageContext";
import { useCallback, useEffect, useMemo, useState } from "react";
import { Pressable, StyleSheet, Text, View } from "react-native";
import { CompositeNavigationProp, NavigationProp, useFocusEffect, useNavigation } from "@react-navigation/native";
import {
  CirclePower,
  PlugZap
} from "lucide-react-native";
import {
  AppButton,
  Card,
  EmptyState,
  ErrorBanner,
  Header,
  LoadingState,
  Screen,
  SectionTitle,
  StatusPill
} from "../../core/components";
import { Device, Rule } from "../../core/api/types";
import { commandUnresolved } from "../../core/api/SocketCommandCoordinator";
import { formatDateTime, formatTime, formatWatts, gridModeLabel, setDisplayTimeZone } from "../../core/format";
import { colors, spacing, typography } from "../../core/theme";
import { useAuth } from "../../application/AuthContext";

import { RootStackParamList, RootTabsParamList } from "../../application/navigationTypes";
import type { NativeStackNavigationProp } from "@react-navigation/native-stack";
import { TileHeader } from "../../core/TileHeader";
import { GenerationPanel } from "../generation/GenerationScreen";
import { SalesPanel } from "../sales/SalesScreen";
import { useFocusedResource } from "../energy/useFocusedResource";
import { useSocketCommandActions } from "../devices/useSocketCommandActions";
import { SocketCommandNotice } from "../devices/SocketCommandNotice";
import { DeviceStatusPill } from "../devices/DeviceStatusPill";
import { batteryFlow } from "./powerBalance";

export function DashboardScreen() {
  const { t } = useLanguage();
  const { api, isDemo } = useAuth();
  const navigation = useNavigation<CompositeNavigationProp<NavigationProp<RootTabsParamList>, NativeStackNavigationProp<RootStackParamList>>>();
  const resource = useFocusedResource("dashboard", useCallback((signal: AbortSignal, force: boolean) =>
    force ? api.refreshDashboard(signal) : api.getDashboard(signal), [api]));
  const dashboard = resource.data;
  const battery = batteryFlow(dashboard?.inverter?.batteryPower, dashboard?.inverter?.batteryPowerValid);
  const [selectedDeviceId, setSelectedDeviceId] = useState<string | null>(null);
  const [commandError, setCommandError] = useState<string | null>(null);
  const command = useSocketCommandActions(api.socketCommands, async () => { resource.invalidate(); await resource.refresh(true); }, setCommandError);
  const commandBusy = command.busy(selectedDeviceId);
  const manualDeviceIds = dashboard?.manualDevices.map(device => device.id).join("|") ?? "";
  useFocusEffect(useCallback(() => {
    let canceled = false;
    if (!isDemo && manualDeviceIds) void Promise.allSettled(manualDeviceIds.split("|").map(id => api.socketCommands.recover(id))).then(results => {
      if (!canceled && results.some(result => result.status === "rejected")) setCommandError("Some command results could not be loaded. Commands require a successful recovery check.");
    });
    return () => { canceled = true; };
  }, [api, isDemo, manualDeviceIds]));

  useEffect(() => {
    if (!dashboard) return;
    setDisplayTimeZone(dashboard.timeZoneId);
    setSelectedDeviceId((current) => pickDeviceId(dashboard.manualDevices, current));
  }, [dashboard]);

  const selectedDevice = useMemo(
    () => dashboard?.manualDevices.find((device) => device.id === selectedDeviceId) ?? null,
    [dashboard?.manualDevices, selectedDeviceId]
  );

  const setSocketState = (isOn: boolean) => selectedDeviceId ? command.send(selectedDeviceId, isOn) : Promise.resolve(false);
  const checkSocketCommand = (release = false) => selectedDeviceId ? command.check(selectedDeviceId, release) : Promise.resolve(false);
  const selectedCommand = selectedDeviceId ? api.socketCommands.get(selectedDeviceId) : null;
  const socketBusy = selectedDeviceId ? api.socketCommands.isRunning(selectedDeviceId) : false;

  if (resource.loading && !dashboard) {
    return <Screen scroll={false}><LoadingState label={t("Loading dashboard...")} /></Screen>;
  }

  return (
    <Screen refreshing={resource.loading} onRefresh={() => void resource.refresh(true)}>
      <Header title={t("Dashboard")} subtitle={t("Your energy at a glance")} />
      <ErrorBanner message={commandError ?? resource.error} />
      <Card style={styles.statusCard}>
        <TileHeader
          title={t("Current generation")}
          subtitle={dashboard?.inverter ? t("Latest reported inverter power · polled {0}", formatTime(dashboard.inverter.timestamp)) : t("Waiting for the first inverter reading")}
          loading={resource.loading}
          onRefresh={() => void resource.refresh(true)}
          onDetails={() => navigation.navigate("InverterDetails")}
        />
        <View style={styles.statusMetrics}>
          <View style={styles.statusMetric}>
            <Text style={styles.metaText}>{t("Solar power")}</Text>
            <Text style={[styles.statusValue, { color: colors.primary }]}>{dashboard?.inverter?.solarPowerValid !== true ? "—" : formatWatts(dashboard?.inverter?.solarProduction)}</Text>
            <Text style={styles.metaText}>{dashboard?.inverter?.solarPowerValid !== true ? t("Awaiting reading") : t("Latest inverter reading")}</Text>
          </View>
          <View style={[styles.statusMetric, styles.statusSeparated]}>
            <Text style={styles.metaText}>{t("Load")}</Text>
            <Text style={styles.statusValue}>{dashboard?.inverter?.loadPowerValid !== true ? "—" : formatWatts(dashboard?.inverter?.loadPower)}</Text>
            <Text style={styles.metaText}>{dashboard?.inverter && dashboard.inverter.loadPowerValid === true ? t("Consumption") : t("Awaiting reading")}</Text>
          </View>
          <View style={styles.statusMetric}>
            <Text style={styles.metaText}>{t("Grid")}</Text>
            <Text style={styles.statusValue}>{dashboard?.inverter && dashboard.inverter.gridPowerValid === true ? formatWatts(Math.abs(dashboard.inverter.gridConsumption)) : "—"}</Text>
            <Text style={styles.metaText}>{dashboard?.inverter && dashboard.inverter.gridPowerValid === true ? gridModeLabel(dashboard.inverter.gridConsumption) : t("Awaiting reading")}</Text>
          </View>
          <View style={[styles.statusMetric, styles.statusSeparated]}>
            <Text style={styles.metaText}>{battery.label}</Text>
            <Text style={styles.statusValue}>{battery.watts === null ? "—" : formatWatts(battery.watts)}</Text>
            <Text style={styles.metaText}>{battery.watts === null ? t("Awaiting reading") : t("Latest inverter reading")}</Text>
          </View>
        </View>
      </Card>
      <GenerationPanel compact
        liveInverter={dashboard?.inverter}
        inverterLoading={resource.loading}
        inverterError={resource.error}
        timeZoneId={dashboard?.timeZoneId}
        onRefreshInverter={() => resource.refresh(true)}
        onDetails={(period, date) => navigation.navigate("SolarEstimateDetails", { period, date })}
      />
      <SalesPanel compact onDetails={(period, date) => navigation.navigate("SalesDetails", { period, date })} />

      <SectionTitle title={t("Manual Override")} />
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
                label={t("Socket ON")}
                icon={CirclePower}
                onPress={() => void setSocketState(true)}
                loading={commandBusy === "on"}
                disabled={!selectedDevice || commandBusy !== null || socketBusy || commandUnresolved(selectedCommand)}
              />
              <AppButton
                label={t("Socket OFF")}
                icon={CirclePower}
                onPress={() => void setSocketState(false)}
                loading={commandBusy === "off"}
                disabled={!selectedDevice || commandBusy !== null || socketBusy || commandUnresolved(selectedCommand)}
                variant="danger"
              />
            </View>
            <SocketCommandNotice command={selectedCommand} disabled={commandBusy !== null || socketBusy}
              onCheck={() => void checkSocketCommand()} onRelease={() => void checkSocketCommand(true)} textStyle={styles.metaText} />
          </>
        ) : (
          <EmptyState title={t("No sockets configured.")} detail={t("Add and enable a socket integration in Settings.")} />
        )}
      </Card>

      <SectionTitle title={t("Rules")} />
      {dashboard?.rules.length ? (
        <View style={styles.list}>
          {dashboard.rules.map((rule) => (
            <RuleLine key={rule.id} rule={rule} />
          ))}
        </View>
      ) : (
        <EmptyState title={t("No rules configured.")} />
      )}

      <SectionTitle title={t("Smart Sockets")} trailing={<Text style={styles.metaText}>{formatDateTime(dashboard?.deviceLastUpdated)}</Text>} />
      {dashboard?.devices.length ? (
        <View style={styles.list}>
          {dashboard.devices.map((device) => (
            <DeviceLine key={device.id} device={device} />
          ))}
        </View>
      ) : (
        <EmptyState title={t("No devices found.")} />
      )}
    </Screen>
  );
}

function DeviceChoice({ device, selected, onPress }: { device: Device; selected: boolean; onPress: () => void }) {
  const demoDisplayName = useDemoDisplayName();
  return (
    <Pressable onPress={onPress} style={[styles.deviceChoice, selected && styles.deviceChoiceSelected]}>
      <View style={styles.deviceChoiceTitle}>
        <PlugZap color={device.online ? colors.primary : colors.subtle} size={16} />
        <Text style={styles.deviceChoiceName} numberOfLines={1}>{demoDisplayName(device.name)}</Text>
      </View>
      <DeviceStatusPill device={device} />
    </Pressable>
  );
}

function RuleLine({ rule }: { rule: Rule }) {
  const demoDisplayName = useDemoDisplayName();
  const { t } = useLanguage();
  return (
    <Card style={styles.rowCard}>
      <View style={styles.rowCopy}>
        <Text style={styles.rowTitle} numberOfLines={1}>{demoDisplayName(rule.name)}</Text>
        <Text style={styles.rowSubtitle}>{t("SOC >= {0}% | every {1}s", rule.socTurnOnThreshold, rule.intervalSeconds)}</Text>
        <Text style={styles.rowSubtitle}>{t("Switched {0} | Checked {1}", formatDateTime(rule.currentStateChangedAt), formatDateTime(rule.lastEvaluated))}</Text>
      </View>
      <View style={styles.rowStatus}>
        <StatusPill label={rule.currentState ? t("ON") : t("OFF")} tone={rule.currentState ? "success" : "neutral"} />
        {!rule.enabled ? <StatusPill label={t("Disabled")} /> : null}
      </View>
    </Card>
  );
}

function DeviceLine({ device }: { device: Device }) {
  const demoDisplayName = useDemoDisplayName();
  const { t } = useLanguage();
  return (
    <Card style={styles.rowCard}>
      <View style={styles.rowCopy}>
        <Text style={styles.rowTitle} numberOfLines={1}>{demoDisplayName(device.name)}</Text>
        <Text style={styles.rowSubtitle}>{t(device.category ?? "Socket")}</Text>
      </View>
      <View style={styles.rowStatus}>
        <Text style={styles.powerText}>{formatWatts(device.currentPowerW)}</Text>
        <DeviceStatusPill device={device} />
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

const styles = StyleSheet.create({
  statusCard: { gap: spacing.lg },
  statusMetrics: { flexDirection: "row", flexWrap: "wrap", gap: spacing.md },
  statusMetric: { flexBasis: "45%", flexGrow: 1, gap: spacing.sm, minWidth: 0 },
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
