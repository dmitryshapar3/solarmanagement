import { useCallback, useEffect, useState } from "react";
import { useRoute, type RouteProp } from "@react-navigation/native";
import type { RootTabsParamList } from "../../application/navigationTypes";
import { Screen, Header, SegmentedControl } from "../../core/components";
import { useAuth } from "../../application/AuthContext";
import { useLanguage } from "../../application/LanguageContext";
import { useFocusedResource } from "./useFocusedResource";
import { ProductionView } from "../generation/ProductionView";
import { ExportView } from "../sales/ExportView";
export function EnergyScreen() {
  const { t } = useLanguage(); const { api } = useAuth(); const [segment, setSegment] = useState<"Production" | "Export">("Production");
  const route = useRoute<RouteProp<RootTabsParamList, "Energy">>();
  useEffect(() => { if (route.params?.segment) setSegment(route.params.segment); }, [route.params?.segment]);
  const inverter = useFocusedResource("energy-dashboard", useCallback((signal: AbortSignal, force: boolean) => force ? api.refreshDashboard(signal) : api.getDashboard(signal), [api]), api);
  return <Screen refreshing={inverter.loading} onRefresh={() => inverter.refresh(true)}><Header title="Energy" /><SegmentedControl options={[{label:t("Production"), value:"Production"},{label:t("Export"),value:"Export"}]} value={segment} onChange={setSegment} />
    {segment === "Production" ? <ProductionView inverter={inverter.data?.inverter} timeZoneId={inverter.data?.timeZoneId} /> : <ExportView />}
  </Screen>;
}
