import { useCallback, useRef, useState } from "react";
import { Keyboard, StyleSheet, Text, View } from "react-native";
import { useFocusEffect } from "@react-navigation/native";
import { CirclePower, RefreshCcw, Zap } from "lucide-react-native";
import {
  AppButton,
  Card,
  EmptyState,
  ErrorBanner,
  Header,
  LoadingState,
  Screen,
  StatusPill,
  TextField
} from "../../core/components";
import { Device } from "../../core/api/types";
import { formatTime, formatWatts } from "../../core/format";
import { colors, spacing, typography } from "../../core/theme";
import { useAuth } from "../../application/AuthContext";

const autoRefreshIntervalMs = 15000;

export function DevicesScreen() {
  const { api } = useAuth();
  const [devices, setDevices] = useState<Device[]>([]);
  const [lastUpdated, setLastUpdated] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [busyDevice, setBusyDevice] = useState<{ id: string; isOn: boolean } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const hasLoadedRef = useRef(false);
  const requestSeq = useRef(0);

  const load = useCallback(async (mode: "initial" | "refresh" | "silent") => {
    const requestId = ++requestSeq.current;
    if (mode === "initial") {
      setLoading(true);
    } else if (mode === "refresh") {
      setRefreshing(true);
    }
    if (mode !== "silent") {
      setError(null);
    }

    try {
      const result = await api.getDevices(mode === "refresh");
      if (requestId === requestSeq.current) {
        setDevices(result.devices);
        setLastUpdated(result.lastUpdated);
        setError(null);
      }
    } catch (ex) {
      if (mode !== "silent" && requestId === requestSeq.current) {
        setError(ex instanceof Error ? ex.message : "Unable to load devices.");
      }
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, [api]);

  useFocusEffect(
    useCallback(() => {
      void load(hasLoadedRef.current ? "silent" : "initial");
      hasLoadedRef.current = true;

      const interval = setInterval(() => void load("silent"), autoRefreshIntervalMs);
      return () => clearInterval(interval);
    }, [load])
  );

  async function setDeviceState(device: Device, isOn: boolean) {
    setBusyDevice({ id: device.id, isOn });
    setError(null);
    try {
      const result = await api.setDeviceState(device.id, isOn);
      // Invalidate any device fetch that started before the toggle so its
      // stale response cannot overwrite the post-toggle state.
      requestSeq.current++;
      setDevices((current) =>
        current.map((item) =>
          item.id === device.id
            ? result.device ?? { ...item, online: true, isOn: result.isOn }
            : item
        )
      );
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Unable to change socket state.");
    } finally {
      setBusyDevice(null);
    }
  }

  async function renameDevice(device: Device, name: string | null) {
    setError(null);
    try {
      const result = await api.renameDevice(device.id, name);
      requestSeq.current++;
      setDevices(current => current.map(item => item.id === device.id ? result : item));
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Unable to save the device name.");
      throw ex;
    }
  }

  if (loading) {
    return (
      <Screen scroll={false}>
        <LoadingState label="Loading devices..." />
      </Screen>
    );
  }

  const countLabel = `${devices.length} socket${devices.length === 1 ? "" : "s"}`;

  return (
    <Screen refreshing={refreshing} onRefresh={() => void load("refresh")}>
      <Header
        title="Devices"
        subtitle={lastUpdated ? `${countLabel} | Updated ${formatTime(lastUpdated)}` : countLabel}
        action={(
          <AppButton
            label="Refresh"
            icon={RefreshCcw}
            onPress={() => void load("refresh")}
            loading={refreshing}
            disabled={refreshing || loading}
            variant="secondary"
            compact
          />
        )}
      />
      <ErrorBanner message={error} />

      {devices.length ? (
        <View style={styles.list}>
          {devices.map((device) => (
            <DeviceCard
              key={device.id}
              device={device}
              busyState={busyDevice?.id === device.id ? busyDevice.isOn : null}
              onTurnOn={() => void setDeviceState(device, true)}
              onTurnOff={() => void setDeviceState(device, false)}
              onRename={name => renameDevice(device, name)}
            />
          ))}
        </View>
      ) : (
        <EmptyState title="No devices found." detail="Configure the Shelly socket backend in Settings." />
      )}
    </Screen>
  );
}

function DeviceCard({
  device,
  busyState,
  onTurnOn,
  onTurnOff,
  onRename
}: {
  device: Device;
  busyState: boolean | null;
  onTurnOn: () => void;
  onTurnOff: () => void;
  onRename: (name: string | null) => Promise<void>;
}) {
  const [editing, setEditing] = useState(false);
  const [name, setName] = useState("");
  const [saving, setSaving] = useState(false);
  const savingRef = useRef(false);
  async function saveName(value: string | null) {
    if (savingRef.current) return;
    savingRef.current = true;
    Keyboard.dismiss();
    setSaving(true);
    try { await onRename(value); setEditing(false); }
    catch { /* The screen displays the server error and preserves the draft. */ }
    finally { savingRef.current = false; setSaving(false); }
  }
  return (
    <Card style={styles.card}>
      <View style={styles.topRow}>
        <View style={styles.titleGroup}>
          <Text style={styles.name} numberOfLines={1}>{device.name}</Text>
          <Text style={styles.category}>{device.category ?? "Socket"}</Text>
          <Text style={styles.category}>{device.id}</Text>
        </View>
        <StatusPill
          label={!device.online ? "Offline" : device.isOn ? "ON" : "OFF"}
          tone={!device.online ? "neutral" : device.isOn ? "success" : "warning"}
        />
      </View>

      {editing ? <View style={styles.nameForm}>
        <TextField label="Name in Solar" value={name} onChangeText={value => setName(value.slice(0, 80))} editable={!saving} />
        <Text style={styles.category}>Shelly name: {device.cloudName ?? device.name}. This changes the display name in Solar; device IDs and rules stay connected.</Text>
        <View style={styles.actions}>
          <AppButton label="Save name" compact onPress={() => void saveName(name.trim() || null)} loading={saving} disabled={saving} />
          <AppButton label="Use Shelly name" compact variant="secondary" onPress={() => void saveName(null)} disabled={saving} />
          <AppButton label="Cancel" compact variant="ghost" onPress={() => setEditing(false)} disabled={saving} />
        </View>
      </View> : <AppButton label="Edit name" compact variant="ghost"
        onPress={() => { setName(device.localName ?? device.name); setEditing(true); }} />}

      <View style={styles.powerRow}>
        <Zap color={colors.amber} size={18} />
        <Text style={styles.power}>{formatWatts(device.currentPowerW)}</Text>
      </View>

      <View style={styles.actions}>
        <AppButton
          label="ON"
          icon={CirclePower}
          onPress={onTurnOn}
          loading={busyState === true}
          disabled={busyState !== null}
          compact
        />
        <AppButton
          label="OFF"
          icon={CirclePower}
          onPress={onTurnOff}
          loading={busyState === false}
          disabled={busyState !== null}
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
    flexWrap: "wrap",
    gap: spacing.md
  },
  nameForm: {
    gap: spacing.md
  }
});
