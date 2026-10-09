import { Pressable, StyleSheet, View } from "react-native";
import type { BottomTabBarProps } from "@react-navigation/bottom-tabs";
import { BlurView } from "expo-blur";
import { House, PlugZap, ChartNoAxesCombined, Workflow } from "lucide-react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { ThemedText as Text } from "../core/components";
import { useTheme } from "../ui/theme/ThemeProvider";
import { designTokens } from "../ui/theme/tokens";

const icons = [House, ChartNoAxesCombined, PlugZap, Workflow];

export function FloatingTabBar({ state, descriptors, navigation }: BottomTabBarProps) {
  const { colors, scheme } = useTheme();
  const insets = useSafeAreaInsets();

  return <View style={[styles.capsule, { bottom: Math.max(26, insets.bottom) }, scheme === "dark" ? designTokens.shadows.tabBarDark : designTokens.shadows.tabBar]}>
    <BlurView intensity={24} tint={scheme} style={[StyleSheet.absoluteFill, { backgroundColor: colors.glass }]} />
    <View style={styles.row}>
      {state.routes.map((route, index) => {
        const selected = state.index === index;
        const options = descriptors[route.key]!.options;
        const Icon = icons[index]!;
        const label = typeof options.tabBarLabel === "string" ? options.tabBarLabel : options.title ?? route.name;
        const color = selected ? colors.onSun : colors.ink3;

        return <Pressable
          key={route.key}
          testID={options.tabBarButtonTestID}
          accessibilityRole="tab"
          accessibilityState={{ selected }}
          accessibilityLabel={options.tabBarAccessibilityLabel ?? label}
          onPress={() => {
            const event = navigation.emit({ type: "tabPress", target: route.key, canPreventDefault: true });
            if (!selected && !event.defaultPrevented) navigation.navigate(route.name, route.params);
          }}
          onLongPress={() => navigation.emit({ type: "tabLongPress", target: route.key })}
          style={[styles.tab, { backgroundColor: selected ? colors.sun : "transparent" }]}
        >
          <Icon size={22} color={color} />
          <Text numberOfLines={1} adjustsFontSizeToFit minimumFontScale={0.75} style={[styles.label, { color }]}>{label}</Text>
        </Pressable>;
      })}
    </View>
  </View>;
}

const styles = StyleSheet.create({
  capsule: { position: "absolute", left: 16, right: 16, height: 64, borderRadius: 32, overflow: "hidden" },
  row: { flexDirection: "row", padding: 6, gap: 2 },
  // A zero basis keeps every tab equal even when translated labels have different widths.
  tab: { flexBasis: 0, flexGrow: 1, flexShrink: 1, minWidth: 0, height: 52, borderRadius: 26, alignItems: "center", justifyContent: "center", gap: 2, paddingHorizontal: 2 },
  label: { width: "100%", minWidth: 0, textAlign: "center", fontSize: 11, lineHeight: 13, fontWeight: "600" }
});
