import { useCallback, useMemo, useState } from "react";
import { Linking, Pressable, View } from "react-native";
import { useNavigation } from "@react-navigation/native";
import type { NativeStackNavigationProp } from "@react-navigation/native-stack";
import { Info, Table2 } from "lucide-react-native";
import { useAuth } from "../../application/AuthContext";
import { useLanguage } from "../../application/LanguageContext";
import type { HomeStackParamList, RootStackParamList } from "../../application/navigationTypes";
import { AppButton, Banner, Card, DataRow, ErrorBanner, LoadingState, SectionTitle, NavigationRow, SegmentedControl, StatusPill, ThemedText as Text } from "../../core/components";
import type { InverterData, SolarEstimateState, SolarHistoryPeriod } from "../../core/api/types";
import type { ProductionHour, ProductionView as ProductionData } from "../../core/api/redesignTypes";
import { useTheme } from "../../ui/theme/ThemeProvider";
import { ProductionChart } from "../../ui/charts/ProductionChart";
import { addDays, amount, canSelectPreviousHistoryDay, dateCaption, known, momentCaption, tickCaption, type ChartPoint, zonedDate } from "../energy/chartPolicy";
import { PeriodNavigation } from "../energy/EnergyControls";
import { useFocusedResource } from "../energy/useFocusedResource";

export function productionStatus(hour: ProductionHour, currentTimestamp?: string | null): string {
  if (hour.timestamp === currentTimestamp) return "In progress";
  if (!known(hour.actualKw)) return Date.parse(hour.timestamp) > Date.now() ? "Upcoming" : "No data";
  if (!known(hour.lowerKw) || !known(hour.upperKw)) return "Estimate unavailable";
  return hour.actualKw < hour.lowerKw ? "Below expected" : hour.actualKw > hour.upperKw ? "Above expected" : "Within expected range";
}
export function productionPoints(data: ProductionData, period: SolarHistoryPeriod, t: (...args: any[])=>string): ChartPoint[] {
  if (period !== "Today") return data.days.map(day=>({ timestamp: `${day.date}T12:00:00Z`, label: day.date.slice(8), actual: day.observedEnergyKwh,
    possible: known(day.lowerEnergyKwh)&&known(day.upperEnergyKwh)?{lowerKw:day.lowerEnergyKwh,upperKw:day.upperEnergyKwh}:null,
    description: t("{0} · Produced {1} · Expected {2}",dateCaption(day.date),amount(day.observedEnergyKwh,"kWh"),amount(day.expectedEnergyKwh,"kWh")) }));
  const localHour = (at:string)=>Number(new Date(at).toLocaleString("en-GB",{timeZone:data.timeZoneId,hour:"2-digit",hourCycle:"h23"}));
  const outside = data.hours.some(h=>(localHour(h.timestamp)<5||localHour(h.timestamp)>20)&&((h.actualKw??0)>0||(h.upperKw??0)>0));
  return data.hours.filter(h=>outside||(localHour(h.timestamp)>=5&&localHour(h.timestamp)<=20)).map(hour=>({ timestamp:hour.timestamp,label:tickCaption(hour.timestamp,data.timeZoneId),actual:hour.actualKw,
    possible:known(hour.lowerKw)&&known(hour.upperKw)?{lowerKw:hour.lowerKw,upperKw:hour.upperKw}:null,
    description:t("{0} · Actual {1} · Expected {2} · {3}",momentCaption(hour.timestamp,data.timeZoneId),amount(hour.actualKw,"kW"),known(hour.lowerKw)&&known(hour.upperKw)?`${amount(hour.lowerKw,"kW")}–${amount(hour.upperKw,"kW")}`:"—",t(productionStatus(hour,data.currentHour?.timestamp))) }));
}
export function WeatherAttribution() { const { colors } = useTheme(); const { t } = useLanguage(); return <Pressable accessibilityRole="link" accessibilityLabel={t("Open-Meteo weather · CC BY 4.0")} onPress={()=>void Linking.openURL("https://open-meteo.com/")} style={{ minHeight:44,justifyContent:"center",paddingHorizontal:4 }}><Text style={{ fontSize:13,lineHeight:18,color:colors.ink2 }}>{t("Weather data by Open-Meteo.com, CC BY 4.0")}</Text></Pressable>; }
export function ProductionView({ inverter, timeZoneId }: { inverter?: InverterData | null; timeZoneId?: string }) {
  const { api } = useAuth(); const { t } = useLanguage(); const { colors } = useTheme(); const navigation=useNavigation<NativeStackNavigationProp<RootStackParamList & HomeStackParamList>>();
  const [period,setPeriod]=useState<SolarHistoryPeriod>("Today"); const [date,setDate]=useState<string>(); const [how,setHow]=useState(false); const [chartIndex,setChartIndex]=useState<number>();
  const resource=useFocusedResource(`production:${period}:${date??"today"}`,useCallback((signal:AbortSignal)=>api.getProduction(period,date,signal),[api,period,date]),api);
  const estimate=useFocusedResource("production-comparison",useCallback((signal:AbortSignal)=>api.getSolarEstimate(signal),[api]),api);
  const data=resource.data; const today=data?.today??zonedDate(new Date(),timeZoneId);const selected=data?.date??date??today;
  const points=useMemo(()=>data?productionPoints(data,period,t):[],[data,period,t]);
  return <><SegmentedControl options={[{label:"Day",value:"Today"},{label:"7 days",value:"Week"},{label:"30 days",value:"Month"}]} value={period} onChange={setPeriod} />
    <PeriodNavigation caption={dateCaption(selected)} previous={canSelectPreviousHistoryDay(selected,today)} next={selected<today} onPrevious={()=>setDate(addDays(selected,-1))} onNext={()=>setDate(addDays(selected,1))} onToday={()=>{setDate(undefined);setPeriod("Today");}} />
    <ErrorBanner message={resource.error??estimate.error} />
    {resource.loading&&!data?<LoadingState label="Loading generation history..."/>:<>
      <View style={{flexDirection:"row",gap:8}}><Stat label="Produced" value={amount(data?.observedEnergyKwh,"kWh")} /><Stat label="Expected" value={amount(data?.expectedEnergyKwh,"kWh")} /></View><View style={{flexDirection:"row",gap:8}}><Stat label="Best hour" value={data?.bestHour ? amount(data.bestHour.actualKw,"kW") : "—"} detail={data?.bestHour ? momentCaption(data.bestHour.timestamp,data.timeZoneId) : undefined}/><Stat label="Right now" value={inverter?.solarPowerValid===true && inverter.solarProduction>=0 ? amount(inverter.solarProduction/1000,"kW") : "—"} /></View>
      <Card style={{gap:14,padding:16}}><SectionTitle title="Production" trailing={<StatusPill label={data?.partial?"Partial data":"Measured"} />} />
        <View style={{flexDirection:"row",gap:16}}><Text style={{fontSize:13,color:colors.solar}}>{t("━ Actual")}</Text><Text style={{fontSize:13,color:colors.ink3}}>{t("■ Expected range")}</Text></View>
        <ProductionChart key={`${period}:${selected}`} points={points} onSelect={setChartIndex} bars={period!=="Today"} unit={period==="Today"?"kW":"kWh"} now={selected===today?new Date().toISOString():null} />
        <View style={{flexDirection:"row",gap:8}}><View style={{flex:1}}><AppButton label="Hourly table" icon={Table2} variant="quiet" compact onPress={()=>navigation.navigate("ProductionHourlySheet",{period,date:selected,selectedTimestamp:chartIndex===undefined?undefined:points[chartIndex]?.timestamp})} /></View><View style={{flex:1}}><AppButton label="How it works" icon={Info} variant="quiet" compact onPress={()=>setHow(v=>!v)} /></View></View>
        {data?.partial?<Text style={{fontSize:13,color:colors.ink2}}>{t("Partial data · totals for available intervals. Missing readings are not zero.")}</Text>:null}
        <ErrorBanner message={data?.actualError??data?.weatherError} />
      </Card>
    </>}
    <SolarComparison state={estimate.data} inverter={inverter} timeZone={timeZoneId??data?.timeZoneId??"Europe/Warsaw"} />
    {how?<><ProductionExplanation state={estimate.data} data={data} /><NavigationRow title="Solar site" onPress={()=>navigation.navigate("MainTabs", { screen: "HomeTab", params: { screen: "SolarSite" } })} /></>:null}<WeatherAttribution />
  </>;
}
function Stat({label,value,detail}:{label:string;value:string;detail?:string}){const{colors}=useTheme();const{t}=useLanguage();return<Card style={{flex:1,padding:16,gap:4}}><Text style={{fontSize:13,color:colors.ink2}}>{t(label)}</Text><Text style={{fontSize:24,lineHeight:28,fontWeight:"700",fontVariant:["tabular-nums"]}}>{value}</Text>{detail?<Text style={{fontSize:13,color:colors.ink3}}>{detail}</Text>:null}</Card>;}
export function SolarComparison({state,inverter,timeZone}:{state:SolarEstimateState|null;inverter?:InverterData|null;timeZone:string}) {
  const{colors}=useTheme();const{t}=useLanguage();const valid=state&&!state.error&&!state.refreshFailed&&state.comparison.status!==0;
  const current=state&&!state.error&&!state.refreshFailed?state.estimate:null;const actual=inverter?.solarPowerValid===true&&inverter.solarProduction>=0?inverter.solarProduction/1000:null;
  const statuses=["Comparison unavailable","Within expected range","Below expected","Above expected"];
  return<Card style={{gap:14}}><SectionTitle title="Right now"/><View style={{flexDirection:"row",gap:16}}>
    <View style={{flex:1,gap:3}}><Text style={{fontSize:13,color:colors.ink2}}>{t("Inverter")}</Text><Text style={{fontSize:24,lineHeight:28,fontWeight:"700"}}>{amount(actual,"kW")}</Text><Text style={{fontSize:13,color:colors.ink2}}>{inverter?.solarObservedAt?momentCaption(inverter.solarObservedAt,timeZone):t("Measurement time unavailable")}</Text></View>
    <View style={{flex:1,gap:3}}><Text style={{fontSize:13,color:colors.ink2}}>{t("Weather estimate")}</Text><Text style={{fontSize:24,lineHeight:28,fontWeight:"700"}}>{amount(current?.centralKw,"kW")}</Text><Text style={{fontSize:13,color:colors.ink2}}>{current?t("Range {0}–{1}",amount(current.lowerKw,"kW"),amount(current.upperKw,"kW")):t("Estimate unavailable")}</Text></View>
  </View><Banner><StatusPill label={valid?statuses[state.comparison.status]! : "Comparison unavailable"} tone={valid&&state.comparison.status===1?"success":"neutral"}/>{valid&&state.comparison.actual?<Text style={{fontSize:13,color:colors.ink2,marginTop:6}}>{t("Compared at {0} · weather model",momentCaption(state.comparison.actual.timestamp,timeZone))}</Text>:<Text style={{fontSize:13,color:colors.ink2,marginTop:6}}>{t(state?.comparison.reason??"A verified comparison is not available.")}</Text>}</Banner></Card>;
}
export function ProductionExplanation({state,data}:{state:SolarEstimateState|null;data:ProductionData|null}) {
  const{t}=useLanguage();const{colors}=useTheme();const zone=data?.timeZoneId??"Europe/Warsaw";const estimate=state?.estimate;const comparison=state?.comparisonEstimate;
  return<Card style={{gap:12}}><SectionTitle title="How it works"/><Text style={{fontSize:15,color:colors.ink2}}>{t("The shaded band is a weather estimate. The line shows measured PV power. Missing readings remain gaps.")}</Text>
    <Text style={{fontSize:13,color:colors.ink2}}>{t("Measured energy integrates observed intervals only. Current power, hourly averages and daily energy use different units.")}</Text>
    {estimate?<><DataRow label="Central estimate" value={amount(estimate.centralKw,"kW")}/><DataRow label="Installed solar capacity" value={amount(estimate.totalKwp,"kWp")}/><DataRow label="Power basis" value={t(["PV DC generation","Inverter AC power","Grid export"][estimate.basis]!)} /><DataRow label="Estimate time" value={momentCaption(estimate.timestamp,zone)}/><DataRow label="Calculated at" value={momentCaption(estimate.calculatedAt,zone)}/><DataRow label="Radiation observation" value={momentCaption(estimate.observation.timestamp,zone)} />{estimate.observation.retrievedAt?<DataRow label="Weather retrieved" value={momentCaption(estimate.observation.retrievedAt,zone)}/>:null}{estimate.observation.weatherTimestamp?<DataRow label="Weather observation" value={momentCaption(estimate.observation.weatherTimestamp,zone)}/>:null}</>:null}
    {state?.comparison.actual?<DataRow label="Historical inverter power" value={`${amount(state.comparison.actual.powerKw,"kW")} · ${momentCaption(state.comparison.actual.timestamp,zone)}`}/>:null}
    {comparison?<DataRow label="Estimate for comparison" value={`${amount(comparison.centralKw,"kW")} · ${momentCaption(comparison.timestamp,zone)}`}/>:null}
    <Text style={{fontSize:13,color:colors.ink2}}>{t("The server checks measurement type, age and timestamp alignment. This historical inverter value can differ from the latest reading above. A percentage comparison may be unavailable at night or at very low power.")}</Text><ErrorBanner message={state?.error}/>
  </Card>;
}
