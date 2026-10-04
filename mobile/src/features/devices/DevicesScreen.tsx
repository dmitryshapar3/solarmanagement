import { useDemoDisplayName } from "../demo/useDemoDisplayName";
import { useLanguage } from "../../application/LanguageContext";
import { useCallback, useEffect, useRef, useState } from "react";
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
import { commandUnresolved, socketCommandMessage, type SocketCommandState } from "../../core/api/SocketCommandCoordinator";
import { formatTime, formatWatts } from "../../core/format";
import { colors, spacing, typography } from "../../core/theme";
import { useAuth } from "../../application/AuthContext";

const autoRefreshIntervalMs = 15000;

export function DevicesScreen() {
  const { t } = useLanguage();
  const { api, isDemo } = useAuth();
  const [devices, setDevices] = useState<Device[]>([]);
  const [lastUpdated, setLastUpdated] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [busyDevice, setBusyDevice] = useState<{ id: string; isOn: boolean } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const hasLoadedRef = useRef(false);
  const requestSeq = useRef(0);
  const viewGeneration = useRef(0);
  const active = useRef(false);
  const [, setCommandRevision] = useState(0);
  useEffect(() => api.socketCommands.subscribe(() => setCommandRevision(value => value + 1)), [api]);

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

  useFocusEffect(
    useCallback(() => {
      active.current = true;
      ++viewGeneration.current;
      setBusyDevice(null);
      void load(hasLoadedRef.current ? "silent" : "initial", true);
      hasLoadedRef.current = true;

      const interval = setInterval(() => void load("silent"), autoRefreshIntervalMs);
      return () => { clearInterval(interval); active.current = false; ++viewGeneration.current; ++requestSeq.current; };
    }, [load])
  );

  async function setDeviceState(device: Device, isOn: boolean) {
    if (api.socketCommands.isRunning(device.id)) return;
    const generation = viewGeneration.current;
    const current = () => active.current && generation === viewGeneration.current;
    setBusyDevice({ id: device.id, isOn });
    setError(null);
    try {
      let acknowledged: boolean;
      if (isDemo) { await api.setDeviceState(device.id, isOn); acknowledged = true; }
      else acknowledged = (await api.socketCommands.send(device.id, isOn)).status === "acknowledged";
      if (current() && acknowledged) { requestSeq.current++; await load("refresh"); }
    } catch (ex) {
      if (current()) setError(ex instanceof Error ? ex.message : "Unable to change socket state.");
    } finally {
      if (current()) setBusyDevice(null);
    }
  }

  async function checkDeviceCommand(device: Device, release = false) {
    if (api.socketCommands.isRunning(device.id)) return;
    const generation = viewGeneration.current;
    const current = () => active.current && generation === viewGeneration.current;
    setBusyDevice({ id: device.id, isOn: api.socketCommands.get(device.id)?.isOn ?? false });
    setError(null);
    try {
      const receipt = await api.socketCommands.check(device.id, release);
      if (current() && receipt.status === "acknowledged") { requestSeq.current++; await load("refresh"); }
    } catch (ex) { if (current()) setError(ex instanceof Error ? ex.message : "Unable to check the command result."); }
    finally { if (current()) setBusyDevice(null); }
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
        <LoadingState label={t("Loading devices...")} />
      </Screen>
    );
  }

  const countLabel = devices.length === 1 ? t("{0} socket", devices.length) : t("{0} sockets", devices.length);

  return (
    <Screen refreshing={refreshing} onRefresh={() => void load("refresh")}>
      <Header
        title={t("Devices")}
        subtitle={lastUpdated ? t("{0} | Updated {1}", countLabel, formatTime(lastUpdated)) : countLabel}
        action={(
          <AppButton
            label={t("Refresh")}
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
              command={api.socketCommands.get(device.id)}
              commandRunning={api.socketCommands.isRunning(device.id)}
              onCheckCommand={() => void checkDeviceCommand(device)}
              onReleaseCommand={() => void checkDeviceCommand(device, true)}
              onTurnOn={() => void setDeviceState(device, true)}
              onTurnOff={() => void setDeviceState(device, false)}
              onRename={name => renameDevice(device, name)}
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
  command,
  commandRunning,
  onCheckCommand,
  onReleaseCommand
}: {
  device: Device;
  busyState: boolean | null;
  onTurnOn: () => void;
  onTurnOff: () => void;
  onRename: (name: string | null) => Promise<void>;
  command: SocketCommandState | null;
  commandRunning: boolean;
  onCheckCommand: () => void;
  onReleaseCommand: () => void;
}) {
  const demoDisplayName = useDemoDisplayName();
  const { t } = useLanguage();
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
          <Text style={styles.name} numberOfLines={1}>{demoDisplayName(device.name)}</Text>
          <Text style={styles.category}>{t(device.category ?? "Socket")}</Text>
          <Text style={styles.category}>{device.id}</Text>
        </View>
        <StatusPill
          label={!device.online ? t("Offline") : device.isOn ? t("ON") : t("OFF")}
          tone={!device.online ? "neutral" : device.isOn ? "success" : "warning"}
        />
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
      {command ? <Text style={styles.category}>{socketCommandMessage(command)}</Text> : null}
      {commandUnresolved(command) ? <AppButton label={t("Check command result")} variant="secondary" onPress={onCheckCommand}
        disabled={commandRunning || busyState !== null} /> : null}
      {command?.status === "uncertain" ? <>
        <Text style={styles.category}>{t("The earlier operation may still finish; allowing another command does not cancel it. Its result remains unknown. The server must obtain an online device observation first.")}</Text>
        <AppButton label={t("Allow another command")} variant="secondary" onPress={onReleaseCommand} disabled={commandRunning || busyState !== null} />
      </> : null}
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
