import { View } from "react-native";
import { useTheme } from "../theme/ThemeProvider";
export function MiniRangeBar({ actual, lower, upper, maximum }: { actual: number | null; lower: number | null; upper: number | null; maximum: number }) {
 const { colors } = useTheme(); const position=(value:number)=>`${Math.max(0,Math.min(100,value/Math.max(1,maximum)*100))}%` as const;
 const range=lower!==null&&upper!==null&&Number.isFinite(lower)&&Number.isFinite(upper)&&lower>=0&&upper>=lower;
 return <View accessible={false} style={{height:12,justifyContent:"center"}}><View style={{height:5,borderRadius:3,backgroundColor:colors.fill}}>{range?<View style={{position:"absolute",left:position(lower!),width:position(upper!-lower!),height:5,borderRadius:3,backgroundColor:colors.expectedBand}}/>:null}{actual!==null&&Number.isFinite(actual)&&actual>=0?<View style={{position:"absolute",left:position(actual),top:-2,width:9,height:9,marginLeft:-4.5,borderRadius:4.5,backgroundColor:colors.solar,borderWidth:1,borderColor:colors.surface}}/>:null}</View></View>;
}
