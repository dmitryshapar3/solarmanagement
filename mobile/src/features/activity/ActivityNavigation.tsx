import { useNavigation } from "@react-navigation/native";
import type { NativeStackNavigationProp } from "@react-navigation/native-stack";
import type { RootStackParamList } from "../../application/navigationTypes";
import { SegmentedControl } from "../../core/components";

export function ActivityNavigation({ value }: { value: "readings" | "automations" }) {
  const navigation = useNavigation<NativeStackNavigationProp<RootStackParamList>>();
  return <SegmentedControl value={value} options={[{ label: "Readings", value: "readings" }, { label: "Automation", value: "automations" }]}
    onChange={selected => {
      if (selected !== value) navigation.navigate("MainTabs", { screen: "Automations", params: { screen: selected === "readings" ? "ReadingsLog" : "Activity" } });
    }} />;
}
