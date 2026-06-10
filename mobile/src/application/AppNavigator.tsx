import { ActivityIndicator, StyleSheet, Text, View } from "react-native";
import { NavigationContainer, DarkTheme } from "@react-navigation/native";
import { createBottomTabNavigator } from "@react-navigation/bottom-tabs";
import { createNativeStackNavigator } from "@react-navigation/native-stack";
import { History, LayoutDashboard, PlugZap, Settings, SlidersHorizontal } from "lucide-react-native";
import { useAuth } from "./AuthContext";
import { RulesStackParamList } from "./navigationTypes";
import { colors } from "../core/theme";
import { LoginScreen } from "../features/auth/LoginScreen";
import { DashboardScreen } from "../features/dashboard/DashboardScreen";
import { DevicesScreen } from "../features/devices/DevicesScreen";
import { HistoryScreen } from "../features/history/HistoryScreen";
import { RuleEditorScreen } from "../features/rules/RuleEditorScreen";
import { RulesScreen } from "../features/rules/RulesScreen";
import { SettingsScreen } from "../features/settings/SettingsScreen";

type RootTabsParamList = {
  Dashboard: undefined;
  Devices: undefined;
  Rules: undefined;
  History: undefined;
  Settings: undefined;
};

const Tab = createBottomTabNavigator<RootTabsParamList>();
const RulesStack = createNativeStackNavigator<RulesStackParamList>();

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
          tabBarStyle: styles.tabBar,
          tabBarActiveTintColor: colors.primary,
          tabBarInactiveTintColor: colors.subtle,
          tabBarLabelStyle: styles.tabLabel
        }}
      >
        <Tab.Screen
          name="Dashboard"
          component={DashboardScreen}
          options={{
            tabBarIcon: ({ color, size }) => <LayoutDashboard color={color} size={size} />
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
          name="Rules"
          component={RulesStackNavigator}
          options={{
            tabBarIcon: ({ color, size }) => <SlidersHorizontal color={color} size={size} />
          }}
        />
        <Tab.Screen
          name="History"
          component={HistoryScreen}
          options={{
            tabBarIcon: ({ color, size }) => <History color={color} size={size} />
          }}
        />
        <Tab.Screen
          name="Settings"
          component={SettingsScreen}
          options={{
            tabBarIcon: ({ color, size }) => <Settings color={color} size={size} />
          }}
        />
      </Tab.Navigator>
    </NavigationContainer>
  );
}

function RulesStackNavigator() {
  return (
    <RulesStack.Navigator
      screenOptions={{
        headerStyle: { backgroundColor: colors.background },
        headerTintColor: colors.text,
        headerShadowVisible: false,
        contentStyle: { backgroundColor: colors.background }
      }}
    >
      <RulesStack.Screen name="RulesList" component={RulesScreen} options={{ headerShown: false }} />
      <RulesStack.Screen name="RuleEditor" component={RuleEditorScreen} options={{ title: "Rule" }} />
    </RulesStack.Navigator>
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
    height: 68,
    paddingTop: 8,
    paddingBottom: 10,
    backgroundColor: colors.surface,
    borderTopColor: colors.border
  },
  tabLabel: {
    fontSize: 11,
    fontWeight: "700"
  }
});
