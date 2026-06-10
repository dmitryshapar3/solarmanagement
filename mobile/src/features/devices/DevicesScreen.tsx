import { useCallback, useEffect, useState } from "react";
import { StyleSheet, Text, View } from "react-native";
import { CirclePower, RefreshCcw, Zap } from "lucide-react-native";
import {
  AppButton,
  Card,
  EmptyState,
  ErrorBanner,
  Header,
  LoadingState,
  Screen,
  StatusPill
} from "../../core/components";
import { Device } from "../../core/api/types";
import { formatWatts } from "../../core/format";
import { colors, spacing, typography } from "../../core/theme";
import { useAuth } from "../../application/AuthContext";

export function DevicesScreen() {
  const { api } = useAuth();
  const [devices, setDevices] = useState<Device[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [busyDevice, setBusyDevice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async (refresh = false) => {
    setError(null);
    refresh ? setRefreshing(true) : setLoading(true);

    try {
      setDevices(await api.getDevices(refresh));
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Unable to load devices.");
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, [api]);

  useEffect(() => {
    void load(false);
  }, [load]);

  async function setDeviceState(device: Device, isOn: boolean) {
    setBusyDevice(device.id);
    setError(null);
    try {
      await api.setDeviceState(device.id, isOn);
      setDevices((current) =>
        current.map((item) => (item.id === device.id ? { ...item, online: true, isOn } : item))
      );
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Unable to change socket state.");
    } finally {
      setBusyDevice(null);
    }
  }

  if (loading) {
    return (
      <Screen scroll={false}>
        <LoadingState label="Loading devices..." />
      </Screen>
    );
  }

  return (
    <Screen refreshing={refreshing} onRefresh={() => void load(true)}>
      <Header
        title="Devices"
        subtitle={`${devices.length} socket${devices.length === 1 ? "" : "s"}`}
        action={<AppButton label="Refresh" icon={RefreshCcw} onPress={() => void load(true)} variant="secondary" compact />}
      />
      <ErrorBanner message={error} />

      {devices.length ? (
        <View style={styles.list}>
          {devices.map((device) => (
            <DeviceCard
              key={device.id}
              device={device}
              busy={busyDevice === device.id}
              onTurnOn={() => void setDeviceState(device, true)}
              onTurnOff={() => void setDeviceState(device, false)}
            />
          ))}
        </View>
      ) : (
        <EmptyState title="No devices found." detail="Configured socket providers returned no devices." />
      )}
    </Screen>
  );
}

function DeviceCard({
  device,
  busy,
  onTurnOn,
  onTurnOff
}: {
  device: Device;
  busy: boolean;
  onTurnOn: () => void;
  onTurnOff: () => void;
}) {
  return (
    <Card style={styles.card}>
      <View style={styles.topRow}>
        <View style={styles.titleGroup}>
          <Text style={styles.name} numberOfLines={1}>{device.name}</Text>
          <Text style={styles.category}>{device.category ?? "Socket"}</Text>
        </View>
        <StatusPill
          label={!device.online ? "Offline" : device.isOn ? "ON" : "OFF"}
          tone={!device.online ? "neutral" : device.isOn ? "success" : "warning"}
        />
      </View>

      <View style={styles.powerRow}>
        <Zap color={colors.amber} size={18} />
        <Text style={styles.power}>{formatWatts(device.currentPowerW)}</Text>
      </View>

      <View style={styles.actions}>
        <AppButton
          label="ON"
          icon={CirclePower}
          onPress={onTurnOn}
          loading={busy && !device.isOn}
          disabled={busy}
          compact
        />
        <AppButton
          label="OFF"
          icon={CirclePower}
          onPress={onTurnOff}
          loading={busy && device.isOn}
          disabled={busy}
          variant="danger"
          compact
        />
      </View>
    </Card>
  );
}

const styles = StyleSheet.create({
  list: {
    gap: spacing.md
  },
  card: {
    gap: spacing.lg
  },
  topRow: {
    flexDirection: "row",
    alignItems: "flex-start",
    justifyContent: "space-between",
    gap: spacing.md
  },
  titleGroup: {
    flex: 1,
    gap: spacing.xs
  },
  name: {
    color: colors.text,
    fontSize: typography.section,
    fontWeight: "700"
  },
  category: {
    color: colors.muted,
    fontSize: typography.caption
  },
  powerRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: spacing.sm
  },
  power: {
    color: colors.text,
    fontSize: 22,
    fontWeight: "800"
  },
  actions: {
    flexDirection: "row",
    gap: spacing.md
  }
});
