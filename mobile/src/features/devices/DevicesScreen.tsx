import { useCallback } from "react";
import { View } from "react-native";
import { useFocusEffect, useNavigation } from "@react-navigation/native";
import type { NativeStackNavigationProp } from "@react-navigation/native-stack";
import { Plus } from "lucide-react-native";
import { useAuth } from "../../application/AuthContext";
import { useLanguage } from "../../application/LanguageContext";
import type { RootStackParamList } from "../../application/navigationTypes";
import { AppButton, EmptyState, ErrorBanner, Header, LoadingState, Screen, ThemedText as Text } from "../../core/components";
import type { DeviceDetails } from "../../core/api/redesignTypes";
import { formatTime, formatWatts } from "../../core/format";
import { useTheme } from "../../ui/theme/ThemeProvider";
import { useFocusedResource } from "../energy/useFocusedResource";
import { DeviceRow } from "./DeviceRow";
import { readDeviceListHistories } from "./deviceObservationPolicy";

export function DevicesScreen() {
  const { api, isDemo } = useAuth(); const { t } = useLanguage(); const { colors } = useTheme();
  const navigation = useNavigation<NativeStackNavigationProp<RootStackParamList>>();
  const resource = useFocusedResource("devices", useCallback(async (signal: AbortSignal, force: boolean) => {
    const [inventory, rules, permissions] = await Promise.all([api.getDevices(force, signal), api.getRules(), api.accountSecurity.getPermissions(signal)]);
    const descriptions = await Promise.allSettled(inventory.devices.map(device => api.getDeviceDetails(device.id, signal)));
    const details = new Map<string, DeviceDetails>();
    descriptions.forEach((result, index) => { if (result.status === "fulfilled") details.set(inventory.devices[index]!.id, result.value); });
    const histories = await readDeviceListHistories(inventory.devices, details, (id, signal) => api.getDeviceHistory(id, 24, signal), signal);
    return { ...inventory, rules, details, histories, permissions: permissions.permissions };
  }, [api]), api);
  const ids = resource.data?.devices.map(device => device.id).join("|") ?? "";
  useFocusEffect(useCallback(() => {
    if (!isDemo && ids) void Promise.allSettled(ids.split("|").map(id => api.socketCommands.recover(id)));
  }, [api, ids, isDemo]));
  const devices = resource.data?.devices ?? [];
  const watts = devices.filter(device => device.online && device.currentPowerW !== null).reduce((total, device) => total + device.currentPowerW!, 0);
  const anyPower = devices.some(device => device.online && device.currentPowerW !== null);
  return <Screen refreshing={resource.loading} onRefresh={() => resource.refresh(true)}>
    <Header title="Devices" subtitle={t("{0} smart plugs · using {1} now", devices.length, anyPower ? formatWatts(watts) : "—")} />
    <ErrorBanner message={resource.error} />
    {resource.loading && !resource.data ? <LoadingState label="Loading devices..." /> : devices.length ? <View style={{ gap: 12 }}>{devices.map(device => <DeviceRow key={device.id} device={device} details={resource.data?.details.get(device.id)} history={resource.error ? undefined : resource.data?.histories.get(device.id)} rules={resource.data?.rules} permissions={resource.loading || resource.error ? undefined : resource.data?.permissions} onChanged={async () => { resource.invalidate(); await resource.refresh(true); }} />)}</View> : <EmptyState title="No devices yet" detail="Connect a smart plug to use solar when it is available." />}
    {resource.data?.lastUpdated ? <Text style={{ color: colors.ink3, fontSize: 13 }}>{t("Updated {0}", formatTime(resource.data.lastUpdated))}</Text> : null}
    <AppButton label="Connect a plug" icon={Plus} onPress={() => navigation.navigate("Connect", { kind: "socket" })} />
  </Screen>;
}
