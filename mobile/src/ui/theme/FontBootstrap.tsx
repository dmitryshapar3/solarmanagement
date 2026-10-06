import { type ReactNode } from "react";
import { View } from "react-native";
import { useFonts } from "expo-font";
import { useTheme } from "./ThemeProvider";

export const fontAssets = {
  "Onest-Regular": require("../../../assets/fonts/Onest-Regular.ttf"),
  "Onest-Medium": require("../../../assets/fonts/Onest-Medium.ttf"),
  "Onest-SemiBold": require("../../../assets/fonts/Onest-SemiBold.ttf"),
  "Onest-Bold": require("../../../assets/fonts/Onest-Bold.ttf"),
  "Unbounded-Bold": require("../../../assets/fonts/Unbounded-Bold.ttf")
};
export function FontBootstrap({ children }: { children: ReactNode }) {
  const [loaded, error] = useFonts(fontAssets);
  const { colors } = useTheme();
  if (!loaded && !error) return <View accessibilityLabel="SmartSolar" style={{ flex: 1, backgroundColor: colors.bg }} />;
  return children;
}
