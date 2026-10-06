import { useCallback } from "react";
import { useRoute, type RouteProp } from "@react-navigation/native";
import { View } from "react-native";
import { useAuth } from "../../application/AuthContext";
import { useLanguage } from "../../application/LanguageContext";
import type { RootStackParamList } from "../../application/navigationTypes";
import { Screen, Header, Card, ErrorBanner, LoadingState, ThemedText as Text } from "../../core/components";
import { useTheme } from "../../ui/theme/ThemeProvider";
import { amount, momentCaption, known } from "../energy/chartPolicy";
import { useFocusedResource } from "../energy/useFocusedResource";
import { MiniRangeBar } from "../../ui/charts/MiniRangeBar";
import { productionStatus, WeatherAttribution } from "./ProductionView";
export function HourlyTableSheet(){const{api}=useAuth();const{t}=useLanguage();const{colors}=useTheme();const route=useRoute<RouteProp<RootStackParamList,"ProductionHourlySheet">>();
 const resource=useFocusedResource(`hourly-production:${route.params?.period??"Today"}:${route.params?.date??"today"}`,useCallback((signal:AbortSignal)=>api.getProduction(route.params?.period??"Today",route.params?.date,signal),[api,route.params?.period,route.params?.date]),api);
 const maximum=Math.max(1,...(resource.data?.hours.flatMap(hour=>[hour.actualKw,hour.upperKw]).filter(known)??[]));
 return<Screen refreshing={resource.loading} onRefresh={()=>resource.refresh()}><Header title="Hourly table" subtitle="Hourly average power · kW"/><ErrorBanner message={resource.error}/>{resource.loading&&!resource.data?<LoadingState/>:null}<Card style={{padding:16,gap:0}}>
 {resource.data?.hours.map(hour=><View key={hour.timestamp} style={{backgroundColor:hour.timestamp===route.params?.selectedTimestamp?colors.selectedColumn:"transparent",paddingVertical:14,borderBottomWidth:1,borderBottomColor:colors.line,gap:6}}><View style={{flexDirection:"row",justifyContent:"space-between",gap:8}}><Text style={{fontSize:15,fontWeight:"600"}}>{momentCaption(hour.timestamp,resource.data!.timeZoneId)}</Text><Text style={{fontSize:17,fontWeight:"700"}}>{amount(hour.actualKw,"kW")}</Text></View><View style={{flexDirection:"row",justifyContent:"space-between",gap:8}}><Text style={{fontSize:13,color:colors.ink3}}>{t("Expected")}: {known(hour.lowerKw)&&known(hour.upperKw)?`${amount(hour.lowerKw,"kW")}–${amount(hour.upperKw,"kW")}`:"—"}</Text><Text style={{fontSize:13,color:colors.ink2}}>{t(productionStatus(hour,resource.data?.currentHour?.timestamp))}</Text></View><MiniRangeBar actual={hour.actualKw} lower={hour.lowerKw} upper={hour.upperKw} maximum={maximum}/>{hour.partial?<Text style={{fontSize:13,color:colors.ink3}}>{t("Partial interval · {0}% covered",Math.round(hour.coveredSeconds/Math.max(1,hour.expectedSeconds)*100))}</Text>:null}</View>)}
 </Card><WeatherAttribution/></Screen>;
}
