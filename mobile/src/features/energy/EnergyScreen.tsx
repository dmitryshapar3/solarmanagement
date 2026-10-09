import { useCallback, useEffect, useState } from "react";
import { useRoute, type RouteProp } from "@react-navigation/native";
import type { RootTabsParamList } from "../../application/navigationTypes";
import { Screen, Header, SegmentedControl } from "../../core/components";
import { useAuth } from "../../application/AuthContext";
import { useLanguage } from "../../application/LanguageContext";
import { useFocusedResource } from "./useFocusedResource";
import { ProductionView } from "../generation/ProductionView";
import { ExportView } from "../sales/ExportView";
import { EnergyPeriodFilters } from "./EnergyPeriodFilters";
import type { EnergySelection } from "./energySelection";
import { zonedDate } from "./chartPolicy";
export function EnergyScreen() {
  const { t } = useLanguage(); const { api } = useAuth(); const [segment, setSegment] = useState<"Production" | "Export">("Production");
  const [selection, setSelection] = useState<EnergySelection>({ period: "Day" });
  const [reportedToday, setReportedToday] = useState<string>();
  const route = useRoute<RouteProp<RootTabsParamList, "Energy">>();
  useEffect(() => { if (route.params?.segment) setSegment(route.params.segment); }, [route.params?.segment]);
  const inverter = useFocusedResource("energy-dashboard", useCallback((signal: AbortSignal, force: boolean) => force ? api.refreshDashboard(signal) : api.getDashboard(signal), [api]), api);
  const changeSegment = (value: "Production" | "Export") => {
    setSelection(current => current.date ? current : { ...current, date: reportedToday ?? zonedDate(new Date(), inverter.data?.timeZoneId) });
    setSegment(value);
  };
  return <Screen refreshing={inverter.loading} onRefresh={() => inverter.refresh(true)}><Header title="Energy" /><SegmentedControl options={[{label:t("Production"), value:"Production"},{label:t("Export"),value:"Export"}]} value={segment} onChange={changeSegment} />
    <EnergyPeriodFilters selection={selection} today={reportedToday ?? zonedDate(new Date(), inverter.data?.timeZoneId)} onChange={setSelection} />
    {segment === "Production" ? <ProductionView selection={selection} onTodayResolved={setReportedToday} inverter={inverter.data?.inverter} timeZoneId={inverter.data?.timeZoneId} /> : <ExportView selection={selection} onTodayResolved={setReportedToday} />}
  </Screen>;
}
