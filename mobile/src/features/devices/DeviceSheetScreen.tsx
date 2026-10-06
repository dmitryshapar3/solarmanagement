import { useCallback, useRef, useState } from "react";
import { Pressable, View } from "react-native";
import type { NativeStackScreenProps } from "@react-navigation/native-stack";
import { Copy, PlugZap, X } from "lucide-react-native";
import * as Clipboard from "expo-clipboard";
import { useAuth } from "../../application/AuthContext";
import { useLanguage } from "../../application/LanguageContext";
import { useScopedAction } from "../../application/useScopedAction";
import type { RootStackParamList } from "../../application/navigationTypes";
import type { IntegrationConfiguration, IntegrationSourceInverter, RuleConflictChoice } from "../../core/api/IntegrationApi";
import type { DeviceHistory } from "../../core/api/redesignTypes";
import { AppButton, Banner, Card, DataRow, ErrorBanner, Group, Header, IconButton, LoadingState, NavigationRow, Screen, SectionTitle, SegmentedControl, TextField, ThemedText as Text } from "../../core/components";
import { formatDateTime, formatWatts } from "../../core/format";
import { useTheme } from "../../ui/theme/ThemeProvider";
import { useFocusedResource } from "../energy/useFocusedResource";
import { useSocketCommandActions } from "./useSocketCommandActions";
import { SocketCommandNotice } from "./SocketCommandNotice";
import { ManualOverrideSheet } from "./ManualOverrideSheet";
import { deviceStateLabel } from "./DeviceRow";

export function durationCaption(seconds: number | null): string {
  if (seconds === null || !Number.isFinite(seconds)) return "—";
  const minutes = Math.floor(seconds / 60); return `${Math.floor(minutes / 60)} h ${minutes % 60} min`;
}
export function DeviceTimeline({ history }: { history: DeviceHistory }) {
  const { colors } = useTheme(); const { t } = useLanguage();
  const start = Date.parse(history.start); const seconds = (Date.parse(history.end) - start) / 1000;
  return <Card style={{ gap: 12 }}><SectionTitle title="Last 24 hours" /><Text style={{ fontSize: 24, lineHeight: 28, fontWeight: "700" }}>{t("On for {0}", durationCaption(history.onSeconds))}</Text>
    <View accessibilityLabel={t("Device state timeline. Unknown intervals are not counted as off.")} style={{ height: 24, position: "relative", backgroundColor: colors.fill, borderRadius: 8, overflow: "hidden" }}>
      {history.intervals.map((interval, index) => <View key={index} style={{ position: "absolute", top: 0, bottom: 0, left: `${Math.max(0, (Date.parse(interval.from) - start) / 1000 / seconds * 100)}%`, width: `${Math.max(0, Math.min(seconds, (Date.parse(interval.to) - Date.parse(interval.from)) / 1000) / seconds * 100)}%`, backgroundColor: interval.isOn === true ? colors.sun : interval.isOn === false ? colors.lineStrong : colors.fill }} />)}
    </View><View style={{ flexDirection: "row", justifyContent: "space-between" }}><Text style={{ fontSize: 13, color: colors.ink3 }}>{formatDateTime(history.start)}</Text><Text style={{ fontSize: 13, color: colors.ink3 }}>{t("Now")}</Text></View>
    {history.partial ? <Text style={{ color: colors.ink2, fontSize: 13 }}>{t("Partial history · unknown intervals are not counted as off.")}</Text> : null}
  </Card>;
}
export function DeviceSheetScreen({ route, navigation }: NativeStackScreenProps<RootStackParamList, "DeviceSheet">) {
  const { api, isDemo } = useAuth(); const { colors } = useTheme(); const { t } = useLanguage(); const { id } = route.params;
  const [error, setError] = useState<string | null>(null); const [choice, setChoice] = useState<boolean | null>(null);
  const [editing, setEditing] = useState(false); const [name, setName] = useState("");
  const [sourceEditor, setSourceEditor] = useState<{ sources: IntegrationSourceInverter[]; configuration: IntegrationConfiguration } | null>(null);
  const [source, setSource] = useState<string | null>(null); const [phase, setPhase] = useState<"1" | "3">("1");
  const resource = useFocusedResource(`device:${id}`, useCallback(async (signal: AbortSignal) => {
    const [details, permissions] = await Promise.all([api.getDeviceDetails(id, signal), api.accountSecurity.getPermissions(signal)]);
    const history = details.supportsHistory ? await api.getDeviceHistory(id, 24, signal) : null;
    return { details, history, permissions: permissions.permissions };
  }, [api, id]), api);
  const reload = async () => { resource.invalidate(); await resource.refresh(true); };
  const commands = useSocketCommandActions(api.socketCommands, reload, setError);
  const actions = useScopedAction(api, id, () => { setEditing(false); setName(""); setSourceEditor(null); setChoice(null); setError(null); });
  const details = resource.data?.details; const device = details?.device; const enabled = details?.controllingRules.filter(rule => rule.enabled) ?? [];
  const command = api.socketCommands.get(id); const busy = commands.busy(id) !== null || api.socketCommands.isRunning(id);
  const canPause = resource.data?.permissions.includes("ManageRules") === true;
  const disabled = isDemo || resource.loading || Boolean(resource.error) || resource.data?.permissions.includes("ControlDevices") !== true || busy || !details?.canSwitch || !device?.online || !device.stateKnown || command?.status === "pending" || command?.status === "uncertain";
  const epoch = api.sessionEpoch; const guard = useRef({ api, epoch, id, disabled, canPause }); guard.current = { api, epoch, id, disabled, canPause };
  const owns = () => guard.current.api === api && guard.current.epoch === api.sessionEpoch && epoch === api.sessionEpoch && guard.current.id === id;
  const allowed = (option?: RuleConflictChoice) => owns() && !guard.current.disabled && (option !== "pause" || guard.current.canPause);
  const requestSwitch = (isOn: boolean) => { if (!allowed()) return; if (enabled.length) setChoice(isOn); else void commands.send(id, isOn); };
  const choose = async (option: RuleConflictChoice) => { if (choice === null || !allowed(option)) return; await commands.send(id, choice, option); if (owns()) setChoice(null); };
  const rename = (value: string | null) => actions.run("name", async context => {
    await api.renameDevice(id, value); context.publish(() => setEditing(false)); await reload();
  }, { started: () => setError(null), failed: exception => setError(exception instanceof Error ? exception.message : "Unable to save the device name.") });
  const openSource = () => actions.run("source-load", async context => {
    if (!details?.instanceId) return;
    const [sources, configuration] = await Promise.all([api.integrations.getSocketSources(context.signal), api.integrations.getConfiguration(details.instanceId, context.signal)]);
    context.publish(() => { setSourceEditor({ sources, configuration }); setSource(details.sourceInverterId); setPhase(details.phaseCount === 3 ? "3" : "1"); });
  }, { failed: exception => setError(exception instanceof Error ? exception.message : "Unable to load device settings.") });
  const saveSource = () => actions.run("source-save", async context => {
    if (!sourceEditor || !details?.instanceId) return;
    const instance = sourceEditor.configuration.instance;
    await api.integrations.setSocketSource(details.instanceId, id, { guard: { expectedRevision: instance.revision, packageVersion: instance.packageVersion, packageDigest: instance.packageDigest, descriptorDigest: instance.descriptorDigest }, sourceInverterId: source, phaseCount: phase === "3" ? 3 : 1, expectedSourceInverterId: details.sourceInverterId, expectedPhaseCount: details.phaseCount === 3 ? 3 : 1 }, context.signal);
    context.publish(() => setSourceEditor(null)); await reload();
  }, { failed: exception => setError(exception instanceof Error ? exception.message : "Unable to save device settings.") });
  return <Screen refreshing={resource.loading || actions.busy !== null} onRefresh={() => resource.refresh(true)}><Header title={details?.name ?? "Device"} action={<IconButton icon={X} accessibilityLabel="Close" onPress={() => navigation.goBack()} />} /><ErrorBanner message={error ?? resource.error} />
    {resource.loading && !details ? <LoadingState /> : details ? <>
      <Text style={{ color: colors.ink2, fontSize: 13 }}>{details.providerDisplayName ?? t("Provider unavailable")}</Text>
      <View style={{ flexDirection: "row", gap: 8 }}><View style={{ flex: 1 }}><AppButton label="Off" variant={device?.stateKnown && !device.isOn ? "sun" : "quiet"} disabled={disabled} loading={commands.busy(id) === "off"} onPress={() => requestSwitch(false)} /></View><View style={{ flex: 1 }}><AppButton label="On" icon={PlugZap} variant={device?.stateKnown && device.isOn ? "sun" : "quiet"} disabled={disabled} loading={commands.busy(id) === "on"} onPress={() => requestSwitch(true)} /></View></View>
      <Text style={{ color: colors.ink2 }}>{device ? t(deviceStateLabel(device)) : t("Unknown")}{device?.currentPowerW !== null && device?.currentPowerW !== undefined ? ` · ${formatWatts(device.currentPowerW)}` : ""}</Text>
      <Text style={{color:colors.ink3,fontSize:13}}>{t("If an enabled automation controls this device, switching by hand offers pause or just this once.")}</Text>{device?.stateKnown && device.isOn && resource.data?.history?.intervals.at(-1)?.isOn===true ? <Text style={{color:colors.ink2,fontSize:13}}>{t("Latest observed on interval began {0}",formatDateTime(resource.data!.history!.intervals.at(-1)!.from))}</Text> : null}
      {details.lastConfirmedSwitch ? <Text style={{ color: colors.ink2, fontSize: 13 }}>{t("Provider acknowledged at {0}", formatDateTime(details.lastConfirmedSwitch))}</Text> : null}
      {isDemo ? <Banner>{t("Switching is turned off in the demo.")}</Banner> : null}
      <SocketCommandNotice command={command} disabled={busy || isDemo} onCheck={() => void commands.check(id)} onRelease={() => void commands.check(id, true)} textStyle={{ color: colors.ink2, fontSize: 13 }} />
      {resource.data?.history ? <DeviceTimeline history={resource.data.history} /> : null}
      <Group>{details.controllingRules.map(rule => <NavigationRow key={rule.id} title="Controlled by" value={rule.name} subtitle={rule.pauseReason === "manual_override" ? "Manual control · rule paused" : rule.enabled ? "Automation enabled" : "Automation paused"} onPress={() => navigation.navigate("AutomationEditor", { id: rule.id })} />)}<NavigationRow title="Name in SmartSolar" value={details.name} onPress={() => { setName(device?.localName ?? details.name); setEditing(true); }} />{details.instanceId ? <><NavigationRow title="Battery & solar source" value={details.sourceInverterId ? t("Selected inverter") : t("Default")} onPress={() => void openSource()} /><NavigationRow title="Circuit" value={t(details.phaseCount === 3 ? "Three-phase" : "Single-phase")} onPress={() => void openSource()} /></> : null}</Group>
      {editing ? <Card style={{ gap: 12 }}><TextField label="Name in SmartSolar" value={name} onChangeText={setName} maxLength={80} editable={actions.busy === null} /><Text style={{ color: colors.ink2, fontSize: 13 }}>{t("Provider name: {0}. Renaming changes only its display name; rules stay connected.", device?.cloudName ?? details.name)}</Text><AppButton label="Save name" loading={actions.busy === "name"} disabled={actions.busy !== null} onPress={() => void rename(name.trim() || null)} /><AppButton label="Use provider name" variant="quiet" disabled={actions.busy !== null} onPress={() => void rename(null)} /><AppButton label="Cancel" variant="ghost" disabled={actions.busy !== null} onPress={() => setEditing(false)} /></Card> : null}
      {sourceEditor ? <Card style={{ gap: 12 }}><SectionTitle title="Battery & solar source" /><Text style={{ fontSize: 13, color: colors.ink2 }}>{t("The default source follows this socket’s configured inverter. Circuit is used for power readings.")}</Text>{[{ id: null, name: t("Default") }, ...sourceEditor.sources].map(item => <Pressable key={item.id ?? "default"} accessibilityRole="radio" accessibilityState={{ selected: source === item.id }} onPress={() => setSource(item.id)} style={{ minHeight: 44, justifyContent: "center", padding: 12, borderRadius: 12, backgroundColor: source === item.id ? colors.sunTint : colors.fill }}><Text>{item.name}</Text></Pressable>)}<SegmentedControl options={[{ label: "Single-phase", value: "1" }, { label: "Three-phase", value: "3" }]} value={phase} onChange={setPhase} /><AppButton label="Save" loading={actions.busy === "source-save"} disabled={actions.busy !== null} onPress={() => void saveSource()} /><AppButton label="Cancel" variant="ghost" disabled={actions.busy !== null} onPress={() => setSourceEditor(null)} /></Card> : null}
      <Card style={{ gap: 12 }}><SectionTitle title="Details for support" /><DataRow label="Provider" value={details.providerId ?? "—"} />{details.model ? <DataRow label="Model" value={details.model} /> : null}<DataRow label="Device ID" value={details.id} /><AppButton label="Copy device ID" icon={Copy} compact variant="quiet" onPress={() => void Clipboard.setStringAsync(details.id)} />{details.addedAt ? <DataRow label="Date added" value={formatDateTime(details.addedAt)} /> : null}</Card>
    </> : null}
    {choice !== null && device ? <ManualOverrideSheet device={device} rules={enabled} isOn={choice} busy={busy} canControl={!disabled} canPause={canPause} onChoose={option => void choose(option)} onCancel={() => setChoice(null)} /> : null}
  </Screen>;
}
