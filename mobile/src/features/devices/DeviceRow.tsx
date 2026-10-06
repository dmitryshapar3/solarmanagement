import { useCallback, useEffect, useRef, useState } from "react";
import { Pressable, View } from "react-native";
import { useNavigation } from "@react-navigation/native";
import type { NativeStackNavigationProp } from "@react-navigation/native-stack";
import { PlugZap } from "lucide-react-native";
import { useAuth } from "../../application/AuthContext";
import { useLanguage } from "../../application/LanguageContext";
import type { RootStackParamList } from "../../application/navigationTypes";
import type { Device, Rule } from "../../core/api/types";
import type { DeviceDetails, DeviceHistory } from "../../core/api/redesignTypes";
import type { AccountPermissions } from "../../core/api/AccountSecurityApi";
import type { RuleConflictChoice } from "../../core/api/IntegrationApi";
import { commandUnresolved } from "../../core/api/SocketCommandCoordinator";
import { Card, ErrorBanner, NativeSwitch, StatusPill, ThemedText as Text } from "../../core/components";
import { formatDateTime, formatWatts } from "../../core/format";
import { useTheme } from "../../ui/theme/ThemeProvider";
import { useDemoDisplayName } from "../demo/useDemoDisplayName";
import { useSocketCommandActions } from "./useSocketCommandActions";
import { ManualOverrideSheet } from "./ManualOverrideSheet";
import { SocketCommandNotice } from "./SocketCommandNotice";
import { currentObservedStateSince } from "./deviceObservationPolicy";

export function deviceStateLabel(device: Device): string {
  return !device.online ? "Offline" : !device.stateKnown ? "Unknown" : device.isOn ? "On" : "Off";
}
export function DeviceRow({ device, rules = [], details, history, permissions, onChanged, compact = false }: {
  device: Device; rules?: Rule[]; details?: DeviceDetails | null; history?: DeviceHistory | null; permissions?: AccountPermissions["permissions"]; onChanged: () => Promise<void>; compact?: boolean;
}) {
  const { api, isDemo } = useAuth(); const { colors } = useTheme(); const { t } = useLanguage(); const name = useDemoDisplayName();
  const navigation = useNavigation<NativeStackNavigationProp<RootStackParamList>>();
  const [error, setError] = useState<string | null>(null); const [choice, setChoice] = useState<boolean | null>(null);
  const actions = useSocketCommandActions(api.socketCommands, onChanged, setError);
  const controlling = (details?.controllingRules ?? rules.filter(rule => rule.entityId.toLowerCase() === device.id.toLowerCase()));
  const enabled = controlling.filter(rule => rule.enabled); const paused = controlling.some(rule => rule.pauseReason === "manual_override");
  const observedSince = currentObservedStateSince(device, history);
  const command = api.socketCommands.get(device.id); const busy = actions.busy(device.id) !== null || api.socketCommands.isRunning(device.id);
  const canPause = permissions?.includes("ManageRules") === true;
  const disabled = isDemo || permissions?.includes("ControlDevices") !== true || !device.online || !device.stateKnown || details?.canSwitch !== true || busy || commandUnresolved(command);
  const epoch = api.sessionEpoch; const guard = useRef({ api, epoch, id: device.id, disabled, canPause }); guard.current = { api, epoch, id: device.id, disabled, canPause };
  useEffect(() => { setChoice(null); setError(null); }, [api, epoch, device.id]);
  const owns = () => guard.current.api === api && guard.current.epoch === api.sessionEpoch && epoch === api.sessionEpoch && guard.current.id === device.id;
  const allowed = (option?: RuleConflictChoice) => owns() && !guard.current.disabled && (option !== "pause" || guard.current.canPause);
  const request = (isOn: boolean) => { if (!allowed()) return; if (enabled.length) setChoice(isOn); else void actions.send(device.id, isOn); };
  const choose = useCallback(async (option: RuleConflictChoice) => { if (choice === null || !allowed(option)) return; await actions.send(device.id, choice, option); if (owns()) setChoice(null); }, [choice, actions, device.id, api, epoch]);
  return <Card style={{ padding: compact ? 16 : 20, gap: 12 }}>
    <View style={{ flexDirection: "row", alignItems: "center", gap: 12 }}>
      <Pressable accessibilityRole="button" accessibilityLabel={t("Open {0}", name(device.name))} onPress={() => navigation.navigate("DeviceSheet", { id: device.id })} style={{ flex: 1, flexDirection: "row", gap: 12, alignItems: "center", minHeight: 44 }}>
        <View style={{ width: 44, height: 44, borderRadius: 14, backgroundColor: device.online && device.stateKnown && device.isOn ? colors.sun : colors.fill, alignItems: "center", justifyContent: "center" }}><PlugZap size={22} color={device.online && device.stateKnown && device.isOn ? colors.onSun : colors.ink2} /></View>
        <View style={{ flex: 1, gap: 3 }}><Text numberOfLines={1} style={{ fontWeight: "600" }}>{name(device.name)}</Text><Text style={{ fontSize: 13, color: colors.ink2 }}>{busy ? t("Sending…") : t(deviceStateLabel(device))}{device.online && device.stateKnown && device.isOn && device.currentPowerW !== null ? ` · ${formatWatts(device.currentPowerW)}` : ""}</Text></View>
      </Pressable>
      <NativeSwitch label={t("Switch {0}", name(device.name))} value={device.stateKnown && device.isOn} disabled={disabled} onValueChange={request} />
    </View>
    {!compact ? <Text style={{ color: colors.ink3, fontSize: 13 }}>{details?.providerDisplayName ?? t("Provider unavailable")}</Text> : null}
    {!compact && observedSince ? <Text style={{ color: colors.ink3, fontSize: 13 }}>{t("Device state observed")} · {t("From")} {formatDateTime(observedSince)}</Text> : null}
    {paused ? <StatusPill label="Manual control · rule paused" /> : controlling[0] ? <StatusPill label={name(controlling[0].name)} tone={enabled.length ? "info" : "neutral"} /> : null}
    {isDemo ? <Text style={{ color: colors.ink2, fontSize: 13 }}>{t("Switching is turned off in the demo.")}</Text> : null}
    <ErrorBanner message={error} />
    <SocketCommandNotice command={command} disabled={busy || isDemo} onCheck={() => void actions.check(device.id)} onRelease={() => void actions.check(device.id, true)} textStyle={{ color: colors.ink2, fontSize: 13 }} />
    {choice !== null ? <ManualOverrideSheet device={{ ...device, name: name(device.name) }} rules={enabled} isOn={choice} busy={busy} canControl={!disabled} canPause={canPause} onChoose={option => void choose(option)} onCancel={() => setChoice(null)} /> : null}
  </Card>;
}
