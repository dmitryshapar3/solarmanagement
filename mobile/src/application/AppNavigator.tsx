import { ActivityIndicator, StyleSheet, Text, View } from "react-native";
import { NavigationContainer, DarkTheme } from "@react-navigation/native";
import { createBottomTabNavigator } from "@react-navigation/bottom-tabs";
import { createNativeStackNavigator } from "@react-navigation/native-stack";
import { Banknote, History, LayoutDashboard, MoreHorizontal, PlugZap, Settings, SlidersHorizontal, SunMedium } from "lucide-react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { useAuth } from "./AuthContext";
import { MoreStackParamList, RootTabsParamList } from "./navigationTypes";
import { AppButton, Card, Header, Screen } from "../core/components";
import { colors } from "../core/theme";
import { LoginScreen } from "../features/auth/LoginScreen";
import { DashboardScreen } from "../features/dashboard/DashboardScreen";
import { DevicesScreen } from "../features/devices/DevicesScreen";
import { HistoryScreen } from "../features/history/HistoryScreen";
import { RuleEditorScreen } from "../features/rules/RuleEditorScreen";
import { RulesScreen } from "../features/rules/RulesScreen";
import { SettingsScreen } from "../features/settings/SettingsScreen";
import { GenerationScreen } from "../features/generation/GenerationScreen";
import { SalesScreen } from "../features/sales/SalesScreen";
import type { NativeStackScreenProps } from "@react-navigation/native-stack";

const Tab = createBottomTabNavigator<RootTabsParamList>();
const MoreStack = createNativeStackNavigator<MoreStackParamList>();

const navigationTheme = {
  ...DarkTheme,
  colors: {
    ...DarkTheme.colors,
    background: colors.background,
    card: colors.surface,
    border: colors.border,
    primary: colors.primary,
    text: colors.text
  }
};

export function AppNavigator() {
  const { isAuthenticated, isBootstrapping } = useAuth();
  const insets = useSafeAreaInsets();

  if (isBootstrapping) {
    return (
      <View style={styles.boot}>
        <ActivityIndicator color={colors.primary} />
        <Text style={styles.bootText}>DeyeSolar</Text>
      </View>
    );
  }

  if (!isAuthenticated) {
    return <LoginScreen />;
  }

  return (
    <NavigationContainer theme={navigationTheme}>
      <Tab.Navigator
        screenOptions={{
          headerShown: false,
          tabBarStyle: [styles.tabBar, { height: 56 + Math.max(insets.bottom, 8), paddingBottom: Math.max(insets.bottom, 8) }],
          tabBarActiveTintColor: colors.primary,
          tabBarInactiveTintColor: colors.subtle,
          tabBarLabelStyle: styles.tabLabel
        }}
      >
        <Tab.Screen
          name="Dashboard"
          component={DashboardScreen}
          options={{
            tabBarLabel: "Home",
            tabBarIcon: ({ color, size }) => <LayoutDashboard color={color} size={size} />
          }}
        />
        <Tab.Screen
          name="Generation"
          component={GenerationScreen}
          options={{
            tabBarIcon: ({ color, size }) => <SunMedium color={color} size={size} />
          }}
        />
        <Tab.Screen
          name="Sales"
          component={SalesScreen}
          options={{
            tabBarIcon: ({ color, size }) => <Banknote color={color} size={size} />
          }}
        />
        <Tab.Screen
          name="Devices"
          component={DevicesScreen}
          options={{
            tabBarIcon: ({ color, size }) => <PlugZap color={color} size={size} />
          }}
        />
        <Tab.Screen
          name="More"
          component={MoreStackNavigator}
          options={{
            tabBarIcon: ({ color, size }) => <MoreHorizontal color={color} size={size} />
          }}
        />
      </Tab.Navigator>
    </NavigationContainer>
  );
}

function MoreStackNavigator() {
  return (
    <MoreStack.Navigator
      screenOptions={{
        headerStyle: { backgroundColor: colors.background },
        headerTintColor: colors.text,
        headerShadowVisible: false,
        contentStyle: { backgroundColor: colors.background },
        headerBackButtonDisplayMode: "minimal",
        title: ""
      }}
    >
      <MoreStack.Screen name="MoreHome" component={MoreScreen} options={{ headerShown: false }} />
      <MoreStack.Screen name="RulesList" component={RulesScreen} />
      <MoreStack.Screen name="RuleEditor" component={RuleEditorScreen} options={{ title: "Rule" }} />
      <MoreStack.Screen name="History" component={HistoryScreen} />
      <MoreStack.Screen name="Settings" component={SettingsScreen} />
    </MoreStack.Navigator>
  );
}

function MoreScreen({ navigation }: NativeStackScreenProps<MoreStackParamList, "MoreHome">) {
  const { username } = useAuth();
  return (
    <Screen>
      <Header title="More" subtitle={username ? `Signed in as ${username}` : "Your Solar installation"} />
      <Card>
        <AppButton label="Automation rules" icon={SlidersHorizontal} variant="ghost" onPress={() => navigation.navigate("RulesList")} />
        <AppButton label="Readings & run history" icon={History} variant="ghost" onPress={() => navigation.navigate("History")} />
        <AppButton label="Settings & account" icon={Settings} variant="ghost" onPress={() => navigation.navigate("Settings")} />
      </Card>
    </Screen>
  );
}

const styles = StyleSheet.create({
  boot: {
    flex: 1,
    alignItems: "center",
    justifyContent: "center",
    gap: 12,
    backgroundColor: colors.background
  },
  bootText: {
    color: colors.text,
    fontSize: 18,
    fontWeight: "800"
  },
  tabBar: {
    paddingTop: 8,
    backgroundColor: colors.surface,
    borderTopColor: colors.border
  },
  tabLabel: {
    fontSize: 11,
    fontWeight: "700"
  }
});
