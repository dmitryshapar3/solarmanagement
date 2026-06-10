import { useCallback, useEffect, useMemo, useState } from "react";
import { Pressable, StyleSheet, Text, View } from "react-native";
import {
  BatteryCharging,
  Cable,
  CirclePower,
  PlugZap,
  RefreshCcw,
  SunMedium,
  Zap
} from "lucide-react-native";
import {
  AppButton,
  Card,
  EmptyState,
  ErrorBanner,
  Header,
  LoadingState,
  MetricTile,
  ProgressBar,
  Screen,
  SectionTitle,
  StatusPill
} from "../../core/components";
import { Dashboard, Device, Rule } from "../../core/api/types";
import { batteryModeLabel, formatDateTime, formatPercent, formatTime, formatWatts, gridModeLabel } from "../../core/format";
import { colors, spacing, typography } from "../../core/theme";
import { useAuth } from "../../application/AuthContext";

export function DashboardScreen() {
  const { api } = useAuth();
  const [dashboard, setDashboard] = useState<Dashboard | null>(null);
  const [selectedDeviceId, setSelectedDeviceId] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [commandBusy, setCommandBusy] = useState<"on" | "off" | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async (mode: "initial" | "refresh" = "refresh") => {
    setError(null);
    if (mode === "initial") {
      setLoading(true);
    } else {
      setRefreshing(true);
    }

    try {
      const data = await api.getDashboard();
      setDashboard(data);
      setSelectedDeviceId((current) => pickDeviceId(data.devices, current));
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Unable to load dashboard.");
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, [api]);

  useEffect(() => {
    void load("initial");
  }, [load]);

  const selectedDevice = useMemo(
    () => dashboard?.devices.find((device) => device.id === selectedDeviceId) ?? null,
    [dashboard?.devices, selectedDeviceId]
  );

  async function setSocketState(isOn: boolean) {
    if (!selectedDeviceId) {
      return;
    }

    setCommandBusy(isOn ? "on" : "off");
    setError(null);
    try {
      await api.setDeviceState(selectedDeviceId, isOn);
      await load();
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Unable to change socket state.");
    } finally {
      setCommandBusy(null);
    }
  }

  if (loading) {
    return (
      <Screen scroll={false}>
        <LoadingState label="Loading dashboard..." />
      </Screen>
    );
  }

  return (
    <Screen refreshing={refreshing} onRefresh={() => void load()}>
      <Header
        title="Dashboard"
        subtitle={dashboard?.inverter ? `Updated ${formatTime(dashboard.inverter.timestamp)}` : "Waiting for first reading"}
        action={<AppButton label="Refresh" icon={RefreshCcw} onPress={() => void load()} variant="secondary" compact />}
      />

      <ErrorBanner message={error} />

      {dashboard?.inverter ? (
        <>
          <Card style={styles.socCard}>
            <View style={styles.socTop}>
              <View>
                <Text style={styles.socLabel}>Battery SOC</Text>
                <Text style={styles.socValue}>{formatPercent(dashboard.inverter.batterySoc)}</Text>
              </View>
              <StatusPill
                label={batteryModeLabel(dashboard.inverter.batteryPower)}
                tone={dashboard.inverter.batteryPower < 0 ? "success" : dashboard.inverter.batteryPower > 0 ? "warning" : "neutral"}
              />
            </View>
            <ProgressBar value={dashboard.inverter.batterySoc} color={socColor(dashboard.inverter.batterySoc)} />
            <View style={styles.socMeta}>
              <Text style={styles.metaText}>{dashboard.inverter.batteryVoltage.toFixed(1)} V</Text>
              <Text style={styles.metaText}>{dashboard.inverter.batteryCurrent.toFixed(1)} A</Text>
              <Text style={styles.metaText}>{dashboard.inverter.batteryTemperature.toFixed(1)} C</Text>
            </View>
          </Card>

          <View style={styles.metricGrid}>
            <MetricTile
              label="Solar"
              value={formatWatts(dashboard.inverter.solarProduction)}
              detail="Production"
              icon={SunMedium}
              color={colors.amber}
            />
            <MetricTile
              label="Battery"
              value={formatWatts(Math.abs(dashboard.inverter.batteryPower))}
              detail={batteryModeLabel(dashboard.inverter.batteryPower)}
              icon={BatteryCharging}
              color={dashboard.inverter.batteryPower < 0 ? colors.primary : colors.amber}
            />
            <MetricTile
              label="Grid"
              value={formatWatts(Math.abs(dashboard.inverter.gridConsumption))}
              detail={gridModeLabel(dashboard.inverter.gridConsumption)}
              icon={Cable}
              color={colors.blue}
            />
            <MetricTile
              label="Load"
              value={formatWatts(dashboard.inverter.loadPower)}
              detail="Consumption"
              icon={Zap}
              color={colors.primary}
            />
          </View>
        </>
      ) : (
        <EmptyState title="No inverter reading yet." detail="The first poll will populate live values." />
      )}

      <SectionTitle title="Manual Override" />
      <Card style={styles.quickActions}>
        {dashboard?.devices.length ? (
          <>
            <View style={styles.devicePicker}>
              {dashboard.devices.map((device) => (
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
                disabled={!selectedDevice}
              />
              <AppButton
                label="Socket OFF"
                icon={CirclePower}
                onPress={() => void setSocketState(false)}
                loading={commandBusy === "off"}
                disabled={!selectedDevice}
                variant="danger"
              />
            </View>
          </>
        ) : (
          <EmptyState title="No sockets loaded." detail="Refresh Devices after configuring a socket provider." />
        )}
      </Card>

      <SectionTitle title="Rules" />
      {dashboard?.rules.length ? (
        <View style={styles.list}>
          {dashboard.rules.slice(0, 5).map((rule) => (
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

  return devices.find((device) => device.online)?.id ?? devices[0]?.id ?? null;
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
  socCard: {
    gap: spacing.lg
  },
  socTop: {
    flexDirection: "row",
    alignItems: "flex-start",
    justifyContent: "space-between",
    gap: spacing.md
  },
  socLabel: {
    color: colors.muted,
    fontSize: typography.caption,
    fontWeight: "700",
    textTransform: "uppercase"
  },
  socValue: {
    color: colors.text,
    fontSize: 52,
    fontWeight: "900"
  },
  socMeta: {
    flexDirection: "row",
    gap: spacing.lg
  },
  metaText: {
    color: colors.muted,
    fontSize: typography.caption
  },
  metricGrid: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: spacing.md
  },
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
