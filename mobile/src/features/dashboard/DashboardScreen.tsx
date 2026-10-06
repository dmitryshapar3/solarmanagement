import { useCallback, useEffect, useMemo } from "react";
import { Pressable, View } from "react-native";
import { useNavigation } from "@react-navigation/native";
import type { NativeStackNavigationProp } from "@react-navigation/native-stack";
import { ChevronRight } from "lucide-react-native";
import { useAuth } from "../../application/AuthContext";
import { useLanguage } from "../../application/LanguageContext";
import type { RootStackParamList } from "../../application/navigationTypes";
import type { DeviceDetails } from "../../core/api/redesignTypes";
import { AppButton, Banner, Card, EmptyState, ErrorBanner, Header, LoadingState, NavigationRow, Screen, SectionTitle, ThemedText as Text } from "../../core/components";
import { formatNumber, formatTime, setDisplayTimeZone } from "../../core/format";
import { freshnessLabel } from "../../core/freshness";
import { useTheme } from "../../ui/theme/ThemeProvider";
import { EnergyFlow } from "../../ui/energy/EnergyFlow";
import { ProductionChart } from "../../ui/charts/ProductionChart";
import { useFocusedResource } from "../energy/useFocusedResource";
import { amount, known, zonedDate } from "../energy/chartPolicy";
import { productionPoints, WeatherAttribution } from "../generation/ProductionView";
import { DeviceRow } from "../devices/DeviceRow";
import { ActivityRow } from "../activity/ActivityRow";
import { FirstRunChecklist } from "../onboarding/FirstRunChecklist";
import { useOptionalSubscription } from "../subscription/SubscriptionContext";
import { homeSunState, homeUsesBattery } from "./homePolicy";

export function DashboardScreen() {
  const { api, isDemo, username, enterDemo } = useAuth(); const { colors } = useTheme(); const { t } = useLanguage();
  const navigation = useNavigation<NativeStackNavigationProp<RootStackParamList>>(); const subscription = useOptionalSubscription();
  const dashboard = useFocusedResource("home-dashboard", useCallback((signal: AbortSignal, force: boolean) => force ? api.refreshDashboard(signal) : api.getDashboard(signal), [api]), api);
  const production = useFocusedResource("home-production", useCallback((signal: AbortSignal) => api.getProduction("Today", undefined, signal), [api]), api);
  const estimate = useFocusedResource("home-estimate", useCallback((signal: AbortSignal) => api.getSolarEstimate(signal), [api]), api);
  const activity = useFocusedResource("home-activity", useCallback((signal: AbortSignal) => api.getActivity({}, signal), [api]), api);
  const zone = dashboard.data?.timeZoneId; const today = zonedDate(new Date(), zone);
  const sales = useFocusedResource(`home-export:${today}`, useCallback(async (signal: AbortSignal) => {
    const site = await api.getSiteSettings(signal);
    return api.getSales("Day", zonedDate(new Date(), site.solarSales.timeZoneId), signal);
  }, [api, today]), api);
  const ids = dashboard.data?.devices.map(device => device.id).join("|") ?? "";
  const setup = useFocusedResource(`home-setup:${ids}`, useCallback(async (signal: AbortSignal) => {
    const [site, sources, permissions] = await Promise.all([api.getSiteSettings(signal), api.integrations.getSocketSources(signal), api.accountSecurity.getPermissions(signal)]);
    const devices = ids ? await Promise.allSettled(ids.split("|").map(id => api.getDeviceDetails(id, signal))) : [];
    const details = new Map<string, DeviceDetails>(); devices.forEach(result => { if (result.status === "fulfilled") details.set(result.value.id, result.value); });
    return { site, sources, details, permissions: permissions.permissions };
  }, [api, ids]), api);
  useEffect(() => { if (zone) setDisplayTimeZone(zone); }, [zone]);
  const points = useMemo(() => production.data ? productionPoints(production.data, "Today", t) : [], [production.data, t]);
  const inverter = dashboard.data?.inverter ?? null; const reading = inverter?.solarPowerValid === true && inverter.solarProduction >= 0 ? inverter.solarProduction / 1000 : null;
  const data = production.data; const astronomy = homeSunState(data); const isNight = astronomy.night;
  const homeOnBattery = isNight && homeUsesBattery(inverter);
  const comparison = estimate.data; const validComparison = comparison && !comparison.error && !comparison.refreshFailed && comparison.comparison.status !== 0;
  const currentEstimate = comparison && !comparison.error && !comparison.refreshFailed ? comparison.estimate : null;
  const statuses = ["Comparison unavailable", "Within expected range", "Below expected", "Above expected"];
  const openEnergy = (segment: "Production" | "Export") => navigation.navigate("MainTabs", { screen: "Energy", params: { segment } });
  const openHome = (screen: "Settings" | "LiveReadings" | "SolarSite") => navigation.navigate("MainTabs", { screen: "HomeTab", params: { screen } });
  const allRules = dashboard.data?.rules ?? [];
  const relevantRules = allRules.filter(rule => rule.enabled && inverter?.inverterId && (rule.sourceInverterId ?? setup.data?.details.get(rule.entityId)?.sourceInverterId ?? setup.data?.sources.find(source => source.isDefault)?.id) === inverter.inverterId);
  const hasInverter = Boolean(setup.data?.sources.length); const hasSite = Boolean(setup.data && setup.data.site.solarEstimate.roof1Kwp + setup.data.site.solarEstimate.roof2Kwp > 0);
  const firstRun = !isDemo && setup.data && (!hasInverter || !hasSite);
  const reload = async () => { dashboard.invalidate(); setup.invalidate(); activity.invalidate(); await Promise.all([dashboard.refresh(true), setup.refresh(true), activity.refresh(true)]); };
  return <Screen refreshing={dashboard.loading || production.loading || estimate.loading || activity.loading || sales.loading || setup.loading} onRefresh={async () => { await Promise.all([dashboard.refresh(true), production.refresh(true), estimate.refresh(true), activity.refresh(true), sales.refresh(true), setup.refresh(true)]); }}>
    <Header title="Home" subtitle={setup.data?.site.solarEstimate.locationLabel || freshnessLabel(inverter?.timestamp, Boolean(dashboard.error), t)} action={<Pressable accessibilityRole="button" accessibilityLabel={t("Account and settings")} onPress={() => openHome("Settings")} onLongPress={__DEV__ ? () => navigation.navigate("DesignGallery") : undefined} style={{ width: 44, height: 44, borderRadius: 22, backgroundColor: colors.ink, alignItems: "center", justifyContent: "center" }}><Text style={{ color: colors.sun, fontSize: 15, fontWeight: "700" }}>{(username ?? "S").split(/[@.\s_-]/).filter(Boolean).slice(0, 2).map(part => part[0]?.toUpperCase()).join("")}</Text></Pressable>} />
    <ErrorBanner message={dashboard.error ?? setup.error} />
    {subscription?.access?.status === "trial" ? <Banner><View style={{ flexDirection: "row", alignItems: "center", gap: 8 }}><View style={{ flex: 1 }}><Text style={{ fontSize: 13, fontWeight: "600" }}>{t("Your free trial")}</Text><Text style={{ fontSize: 13, color: colors.ink2 }}>{t("Ends {0} · one smart plug", new Date(subscription.access.trialEndsAt).toLocaleDateString())}</Text></View><AppButton label="View plans" variant="ghost" compact onPress={() => navigation.navigate("Paywall")} /></View></Banner> : null}
    {firstRun ? <FirstRunChecklist inverter={hasInverter} site={hasSite} plug={Boolean(dashboard.data?.devices.length)} onInverter={() => navigation.navigate("Connect", { kind: "inverter" })} onSite={() => openHome("SolarSite")} onPlug={() => navigation.navigate("Connect", { kind: "socket" })} onExplore={() => void enterDemo()} /> : <>
      {dashboard.loading && !dashboard.data ? <LoadingState label="Loading dashboard..." /> : null}
      <Pressable accessibilityRole="button" accessibilityLabel={t("Solar now. Open production details")} onPress={() => openEnergy("Production")} style={{ backgroundColor: isNight ? colors.surface : colors.sun, borderRadius: 28, padding: 20, gap: 14 }}>
        <View style={{ flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: 8 }}><Text style={{ color: isNight ? colors.ink : colors.onSun, fontSize: 15, fontWeight: "600" }}>{t("Solar now")}</Text><Text style={{ color: isNight ? colors.ink2 : colors.onSun, fontSize: 13 }}>{isDemo ? t("Sample") : freshnessLabel(inverter?.solarObservedAt ?? inverter?.timestamp, Boolean(dashboard.error), t)}</Text></View>
        {isNight ? <><Text style={{ fontSize: 28, lineHeight: 34, fontWeight: "700" }}>{t(homeOnBattery ? "The sun has set. Home runs on battery." : "The sun has set.")}</Text>{astronomy.nextSunrise ? <Text style={{ color: colors.ink2, fontSize: 15 }}>{t("Next sunrise {0}", formatTime(astronomy.nextSunrise))}</Text> : null}</> : <><View style={{ flexDirection: "row", alignItems: "baseline", gap: 6 }}><Text adjustsFontSizeToFit numberOfLines={1} style={{ color: colors.onSun, fontSize: 72, lineHeight: 78, fontWeight: "700", letterSpacing: -2.88 }}>{reading === null ? "—" : formatNumber(reading, 2)}</Text><Text style={{ color: colors.onSun, fontSize: 26, lineHeight: 30, fontWeight: "600" }}>kW</Text></View><View style={{ gap: 6 }}><Text style={{ alignSelf: "flex-start", backgroundColor: colors.onSun, color: colors.sun, borderRadius: 14, paddingVertical: 4, paddingHorizontal: 10, fontSize: 13, fontWeight: "600" }}>{t(validComparison ? statuses[comparison.comparison.status]! : "Comparison unavailable")}</Text><Text style={{ color: colors.onSun, fontSize: 15 }}>{currentEstimate ? t("{0}–{1} kW expected", formatNumber(currentEstimate.lowerKw, 1), formatNumber(currentEstimate.upperKw, 1)) : t("Weather estimate unavailable")}</Text></View></>}
        {data ? <ProductionChart points={points} compact hero={!isNight} now={new Date().toISOString()} /> : null}
        <View style={{ flexDirection: "row", justifyContent: "space-between", gap: 8 }}>{data?.sunrise ? <Text style={{ color: isNight ? colors.ink2 : colors.onSun, fontSize: 12 }}>{t("Sunrise {0}", formatTime(data.sunrise))}</Text> : null}{data?.sunset ? <Text style={{ color: isNight ? colors.ink2 : colors.onSun, fontSize: 12 }}>{t("Sunset {0}", formatTime(data.sunset))}</Text> : null}</View>
        <View style={{ flexDirection: "row", gap: 12, paddingTop: 14, borderTopWidth: 1, borderTopColor: isNight ? colors.line : "rgba(18,18,15,.16)" }}>{[{ label: "Produced today", value: data?.observedEnergyKwh }, { label: "Expected today", value: data?.expectedEnergyKwh }].map(stat => <View key={stat.label} style={{ flex: 1, gap: 2 }}><Text style={{ color: isNight ? colors.ink2 : colors.onSun, fontSize: 13 }}>{t(stat.label)}</Text><Text style={{ color: isNight ? colors.ink : colors.onSun, fontSize: 24, lineHeight: 28, fontWeight: "700" }}>{amount(stat.value, "kWh")}</Text></View>)}</View>
        {data?.partial ? <Text style={{ color: isNight ? colors.ink2 : colors.onSun, fontSize: 13 }}>{t("Partial data · missing readings remain gaps.")}</Text> : null}
      </Pressable><ErrorBanner message={production.error ?? data?.weatherError ?? data?.actualError ?? estimate.error} /><WeatherAttribution />
      <Card style={{ gap: 14 }}><SectionTitle title="Energy flow" trailing={<Link label="Live readings" onPress={() => openHome("LiveReadings")} />} /><EnergyFlow inverter={inverter} thresholds={relevantRules} /></Card>
      <Pressable accessibilityRole="button" accessibilityLabel={t("Exported today. Open export details")} onPress={() => openEnergy("Export")}><Card style={{ gap: 12 }}><SectionTitle title="Exported today" trailing={<ChevronRight size={20} color={colors.ink3} />} /><View style={{ flexDirection: "row", gap: 12 }}><View style={{ flex: 1 }}><Text style={{ fontSize: 24, lineHeight: 28, fontWeight: "700" }}>{amount(sales.data?.exportKwh, "kWh")}</Text><Text style={{ fontSize: 13, color: colors.ink2 }}>{t("To the grid")}</Text></View><View style={{ flex: 1 }}><Text style={{ fontSize: 24, lineHeight: 28, fontWeight: "700" }}>{amount(sales.data?.energyValuePln, "PLN")}</Text><Text style={{ fontSize: 13, color: colors.ink2 }}>{t("Estimated value")}</Text></View></View><Text style={{ fontSize: 13, color: colors.ink2 }}>{isNight && !sales.data?.currentHour ? t("All export hours are complete") : sales.data?.currentHour ? t("Completed hours · current hour in progress, not in totals") : t("Completed hours only")}</Text>{sales.data?.currentHour ? <View style={{ borderWidth: 1, borderStyle: "dashed", borderColor: colors.grid, borderRadius: 14, padding: 12, gap: 6 }}><SectionTitle title="Current hour · in progress" /><Text style={{ fontSize: 19, fontWeight: "600" }}>{amount(sales.data.currentHour.exportKwh, "kWh")} · {amount(sales.data.currentHour.energyValuePln, "PLN")}</Text><Text style={{ fontSize: 13, color: colors.ink2 }}>{t("Not in totals · measured intervals only, without projection to the end of the hour.")}</Text><Text style={{ fontSize: 13, color: colors.ink2 }}>{sales.data.currentHour.observedThrough ? t("Measured through {0}", formatTime(sales.data.currentHour.observedThrough)) : t("Awaiting current-hour readings.")}</Text></View> : null}{sales.data?.isPartial ? <Text style={{ fontSize: 13, color: colors.warningText }}>{t("Partial data · missing measurements and prices are not zero.")}</Text> : null}</Card></Pressable><ErrorBanner message={sales.error ?? sales.data?.dataError ?? sales.data?.priceError} />
      <SectionTitle title="Devices" trailing={<Link label="All devices" onPress={() => navigation.navigate("MainTabs", { screen: "Devices" })} />} />
      {dashboard.data?.devices.length ? dashboard.data.devices.slice(0, 3).map(device => <DeviceRow key={device.id} compact device={device} rules={allRules} details={setup.data?.details.get(device.id)} permissions={setup.loading || setup.error ? undefined : setup.data?.permissions} onChanged={reload} />) : <EmptyState title="No devices yet" detail="Connect a smart plug to use solar when it is available." />}
      <SectionTitle title="Automations" trailing={<Link label="All automations" onPress={() => navigation.navigate("MainTabs", { screen: "Automations", params: { screen: "AutomationsList" } })} />} />
      {allRules.length ? <Card style={{ padding: 4 }}>{allRules.slice(0, 3).map(rule => <NavigationRow key={rule.id} title={rule.name} subtitle={rule.pauseReason === "manual_override" ? "Paused since you switched this device by hand" : !rule.enabled ? "Paused" : rule.currentState ? "Running" : "Waiting"} onPress={() => navigation.navigate("AutomationEditor", { id: rule.id })} />)}</Card> : <EmptyState title="No automations yet" detail="Let your devices follow your solar and battery." />}
      <SectionTitle title="Recent activity" trailing={<Link label="All activity" onPress={() => navigation.navigate("MainTabs", { screen: "Automations", params: { screen: "Activity" } })} />} /><Card style={{ paddingVertical: 4 }}>{activity.data?.items.slice(0, 2).map(item => <ActivityRow key={item.id} item={item} />)}{activity.data && !activity.data.items.length ? <Text style={{ paddingVertical: 16, color: colors.ink2 }}>{t("No activity yet")}</Text> : null}</Card><ErrorBanner message={activity.error} />
    </>}
  </Screen>;
}
function Link({ label, onPress }: { label: string; onPress: () => void }) { const { colors } = useTheme(); const { t } = useLanguage(); return <Pressable accessibilityRole="button" onPress={onPress} style={{ minHeight: 44, justifyContent: "center" }}><Text style={{ color: colors.ink2, fontSize: 15 }}>{t(label)}</Text></Pressable>; }
