import { View } from "react-native";
import { useLanguage } from "../../application/LanguageContext";
import type { Rule } from "../../core/api/types";
import { ThemedText as Text } from "../../core/components";
import { useTheme } from "../theme/ThemeProvider";
export function BatteryChargeBar({ charge, rules }: { charge: number; rules: Rule[] }) {
 const { colors }=useTheme();const {t}=useLanguage();const percent=(value:number)=>`${Math.max(0,Math.min(100,value))}%` as const;
 return <View style={{gap:10}}><View accessibilityRole="progressbar" accessibilityLabel={t("State of charge")} accessibilityValue={{min:0,max:100,now:charge}} style={{height:12,borderRadius:6,backgroundColor:colors.fill,position:"relative"}}><View style={{height:12,width:percent(charge),borderRadius:6,backgroundColor:colors.battery}}/>{rules.flatMap(rule=>[rule.socTurnOffThreshold,rule.socTurnOnThreshold]).filter(Number.isFinite).map((value,index)=><View key={index} style={{position:"absolute",left:percent(value),top:-3,width:1,height:18,backgroundColor:colors.ink}}/>)}</View>{rules.map(rule=><Text key={rule.id} style={{fontSize:13,color:colors.ink2}}>{t("{0}: on {1}% · off {2}%",rule.name,rule.socTurnOnThreshold,rule.socTurnOffThreshold)}</Text>)}</View>;
}
