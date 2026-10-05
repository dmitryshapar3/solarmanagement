import { useDemoDisplayName } from "../demo/useDemoDisplayName";
import { useLanguage } from "../../application/LanguageContext";
import { useCallback, useRef, useState } from "react";
import { Keyboard, StyleSheet, Text, View } from "react-native";
import { useFocusEffect } from "@react-navigation/native";
import { CirclePower, Zap } from "lucide-react-native";
import {
  AppButton,
  Card,
  EmptyState,
  ErrorBanner,
  Header,
  LoadingState,
  Screen,
  TextField
} from "../../core/components";
import { Device } from "../../core/api/types";
import { commandUnresolved, type SocketCommandState } from "../../core/api/SocketCommandCoordinator";
import { formatTime, formatWatts } from "../../core/format";
import { colors, spacing, typography } from "../../core/theme";
import { useSocketCommandActions } from "./useSocketCommandActions";
import { SocketCommandNotice } from "./SocketCommandNotice";
import { listCardHeaderStyles } from "../../core/listCardHeaderStyles";
import { DeviceStatusPill } from "./DeviceStatusPill";
import { useAuth } from "../../application/AuthContext";
import { useScopedAction } from "../../application/useScopedAction";
import type { ScopedActionContext } from "../../application/ScopedActionScope";

const autoRefreshIntervalMs = 15000;

export function DevicesScreen() {
  const { t } = useLanguage();
  const { api, isDemo } = useAuth();
  const [devices, setDevices] = useState<Device[]>([]);
  const [lastUpdated, setLastUpdated] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const hasLoadedRef = useRef(false);
  const requestSeq = useRef(0);
  const load = useCallback(async (mode: "initial" | "refresh" | "silent", recoverCommands = false) => {
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
      if (requestId === requestSeq.current && !isDemo && (recoverCommands || mode === "refresh")) {
        const receipts = await Promise.allSettled(result.devices.map(device => api.socketCommands.recover(device.id)));
        if (requestId === requestSeq.current && receipts.some(receipt => receipt.status === "rejected")) {
          setError("Some command results could not be loaded. Sending a command requires a successful recovery check.");
        }
      }
    } catch (ex) {
      if (mode !== "silent" && requestId === requestSeq.current) {
        setError(ex instanceof Error ? ex.message : "Unable to load devices.");
      }
    } finally {
      if (requestId === requestSeq.current) { setLoading(false); setRefreshing(false); }
    }
  }, [api, isDemo]);

  const commands = useSocketCommandActions(api.socketCommands, async () => { requestSeq.current++; await load("refresh"); }, setError);

  useFocusEffect(
    useCallback(() => {
      void load(hasLoadedRef.current ? "silent" : "initial", true);
      hasLoadedRef.current = true;

      const interval = setInterval(() => void load("silent"), autoRefreshIntervalMs);
      return () => { clearInterval(interval); ++requestSeq.current; };
    }, [load])
  );

  const setDeviceState = (device: Device, isOn: boolean) => commands.send(device.id, isOn);
  const checkDeviceCommand = (device: Device, release = false) => commands.check(device.id, release);

  async function renameDevice(device: Device, name: string | null, context: ScopedActionContext) {
    const result = await api.renameDevice(device.id, name);
    context.publish(() => {
      requestSeq.current++;
      setDevices(current => current.map(item => item.id === device.id ? result : item));
    });
  }

  if (loading) {
    return (
      <Screen scroll={false}>
        <LoadingState label={t("Loading devices...")} />
      </Screen>
    );
  }

  const countLabel = devices.length === 1 ? t("{0} socket", devices.length) : t("{0} sockets", devices.length);

  return (
    <Screen refreshing={refreshing} onRefresh={() => load("refresh")}>
      <Header
        title={t("Devices")}
        subtitle={lastUpdated ? t("{0} | Updated {1}", countLabel, formatTime(lastUpdated)) : countLabel}
      />
      <ErrorBanner message={error} />

      {devices.length ? (
        <View style={styles.list}>
          {devices.map((device) => (
            <DeviceCard
              key={device.id}
              device={device}
              busyState={commands.busy(device.id) === null ? null : commands.busy(device.id) === "on"}
              command={api.socketCommands.get(device.id)}
              commandRunning={api.socketCommands.isRunning(device.id)}
              onCheckCommand={() => void checkDeviceCommand(device)}
              onReleaseCommand={() => void checkDeviceCommand(device, true)}
              onTurnOn={() => void setDeviceState(device, true)}
              onTurnOff={() => void setDeviceState(device, false)}
              onRename={(name, context) => renameDevice(device, name, context)}
              onRenameStarted={() => setError(null)}
              onRenameFailed={exception => setError(exception instanceof Error ? exception.message : "Unable to save the device name.")}
            />
          ))}
        </View>
      ) : (
        <EmptyState title={t("No devices found.")} detail={t("Add and enable a socket integration in Settings.")} />
      )}
    </Screen>
  );
}

function DeviceCard({
  device,
  busyState,
  onTurnOn,
  onTurnOff,
  onRename,
  onRenameStarted,
  onRenameFailed,
  command,
  commandRunning,
  onCheckCommand,
  onReleaseCommand
}: {
  device: Device;
  busyState: boolean | null;
  onTurnOn: () => void;
  onTurnOff: () => void;
  onRename: (name: string | null, context: ScopedActionContext) => Promise<void>;
  onRenameStarted: () => void;
  onRenameFailed: (exception: unknown) => void;
  command: SocketCommandState | null;
  commandRunning: boolean;
  onCheckCommand: () => void;
  onReleaseCommand: () => void;
}) {
  const demoDisplayName = useDemoDisplayName();
  const { t } = useLanguage();
  const { api } = useAuth();
  const [editing, setEditing] = useState(false);
  const [name, setName] = useState("");
  const actions = useScopedAction(api, device.id, () => { setEditing(false); setName(""); });
  const saving = actions.busy !== null;
  const saveName = (value: string | null) => actions.run("rename", async context => {
    await onRename(value, context);
    context.publish(() => setEditing(false));
  }, { started: () => { Keyboard.dismiss(); onRenameStarted(); }, failed: onRenameFailed });
  return (
    <Card style={styles.card}>
      <View style={listCardHeaderStyles.topRow}>
        <View style={listCardHeaderStyles.titleGroup}>
          <Text style={styles.name} numberOfLines={1}>{demoDisplayName(device.name)}</Text>
          <Text style={styles.category}>{t(device.category ?? "Socket")}</Text>
          <Text style={styles.category}>{device.id}</Text>
        </View>
        <DeviceStatusPill device={device} />
      </View>

      {editing ? <View style={styles.nameForm}>
        <TextField label={t("Name in Solar")} value={demoDisplayName(name)} onChangeText={value => setName(value.slice(0, 80))} editable={!saving} />
        <Text style={styles.category}>{t("Provider name: {0}. This changes the display name in Solar; device IDs and rules stay connected.", demoDisplayName(device.cloudName ?? device.name))}</Text>
        <View style={styles.actions}>
          <AppButton label={t("Save name")} compact onPress={() => void saveName(name.trim() || null)} loading={saving} disabled={saving} />
          <AppButton label={t("Use provider name")} compact variant="secondary" onPress={() => void saveName(null)} disabled={saving} />
          <AppButton label={t("Cancel")} compact variant="ghost" onPress={() => setEditing(false)} disabled={saving} />
        </View>
      </View> : <AppButton label={t("Edit name")} compact variant="ghost"
        onPress={() => { setName(device.localName ?? device.name); setEditing(true); }} />}

      <View style={styles.powerRow}>
        <Zap color={colors.amber} size={18} />
        <Text style={styles.power}>{formatWatts(device.currentPowerW)}</Text>
      </View>

      <View style={styles.actions}>
        <AppButton
          label={t("ON")}
          icon={CirclePower}
          onPress={onTurnOn}
          loading={busyState === true}
          disabled={busyState !== null || commandRunning || commandUnresolved(command)}
          compact
        />
        <AppButton
          label={t("OFF")}
          icon={CirclePower}
          onPress={onTurnOff}
          loading={busyState === false}
          disabled={busyState !== null || commandRunning || commandUnresolved(command)}
          variant="danger"
          compact
        />
      </View>
      <SocketCommandNotice command={command} disabled={commandRunning || busyState !== null}
        onCheck={onCheckCommand} onRelease={onReleaseCommand} textStyle={styles.category} />
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
