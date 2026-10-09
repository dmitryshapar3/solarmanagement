import { useCallback, useState } from "react";
import { View } from "react-native";
import { useRoute, type RouteProp } from "@react-navigation/native";
import { useAuth } from "../../application/AuthContext";
import { useLanguage } from "../../application/LanguageContext";
import type { RootStackParamList } from "../../application/navigationTypes";
import { AppButton, Card, ErrorBanner, Header, LoadingState, Screen, ThemedText as Text } from "../../core/components";
import { useTheme } from "../../ui/theme/ThemeProvider";
import { amount, momentCaption, zonedDate } from "../energy/chartPolicy";
import { useFocusedResource } from "../energy/useFocusedResource";

export function ExportHourlySheet() {
  const { api } = useAuth(); const { colors } = useTheme(); const { t } = useLanguage(); const route = useRoute<RouteProp<RootStackParamList, "ExportHourlySheet">>();
  const [limit, setLimit] = useState(72); const params = route.params;
  const resource = useFocusedResource(`export-hours:${params?.period}:${params?.date}:${params?.from}:${params?.through}:${params?.includeUpcoming}`, useCallback(async (signal: AbortSignal) => {
    const site = await api.getSiteSettings(signal);
    return api.getSalesDetails(params?.period ?? "Day", params?.date ?? zonedDate(new Date(), site.solarSales.timeZoneId), signal, params?.from && params.through ? { from: params.from, through: params.through } : undefined, { includeUpcoming: params?.includeUpcoming });
  }, [api, params?.period, params?.date, params?.from, params?.through, params?.includeUpcoming]), api);
  const data = resource.data; const hours = [...data?.hours ?? []].reverse();
  return <Screen refreshing={resource.loading} onRefresh={() => resource.refresh(true)}><Header title="Hourly export" subtitle="Completed hours only · current hour excluded" /><ErrorBanner message={resource.error} />{resource.loading && !data ? <LoadingState /> : null}
    <Card style={{ gap: 0, padding: 16 }}>{hours.slice(0, limit).map(hour => <View key={hour.start} style={{ gap: 6, paddingVertical: 14, borderBottomWidth: 1, borderBottomColor: colors.line }}><View style={{ flexDirection: "row", justifyContent: "space-between", gap: 12 }}><Text style={{ fontSize: 15, fontWeight: "600" }}>{momentCaption(hour.start, data!.timeZoneId)}</Text><Text style={{ fontWeight: "700" }}>{amount(hour.exportKwh, "kWh")}</Text></View><View style={{ flexDirection: "row", justifyContent: "space-between", gap: 12 }}><Text style={{ fontSize: 13, color: colors.ink2 }}>{t("After netting {0}", amount(hour.creditedExportKwh, "kWh"))}</Text><Text style={{ fontSize: 13, color: colors.ink2 }}>{amount(hour.energyValuePln, "PLN")}</Text></View><Text style={{ color: (hour.marketAveragePricePlnPerKwh === undefined ? hour.averagePricePlnPerKwh : hour.marketAveragePricePlnPerKwh) === null ? colors.warningText : colors.ink3, fontSize: 13 }}>{(hour.marketAveragePricePlnPerKwh === undefined ? hour.averagePricePlnPerKwh : hour.marketAveragePricePlnPerKwh) === null ? t("Price unavailable") : `${t("Sale price")} ${amount(hour.marketAveragePricePlnPerKwh === undefined ? hour.averagePricePlnPerKwh : hour.marketAveragePricePlnPerKwh, "PLN/kWh", 6)}`}{hour.observedSeconds < 3600 ? ` · ${t("Partial interval · {0}% covered", Math.round(hour.observedSeconds / 36))}` : ""}</Text></View>)}{!hours.length && data ? <Text style={{ color: colors.ink2, paddingVertical: 16 }}>{t("No completed intervals are available for this period.")}</Text> : null}</Card>
    {hours.length > limit ? <AppButton label="Show earlier hours" variant="quiet" onPress={() => setLimit(value => value + 72)} /> : null}
    <Text style={{ color: colors.ink2, fontSize: 13 }}>{t("Values come from server settlement calculations. Missing readings and prices remain unavailable.")}</Text>
  </Screen>;
}
