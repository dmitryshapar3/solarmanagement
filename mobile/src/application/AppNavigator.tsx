import { useLanguage } from "./LanguageContext";
import { useState } from "react";
import { ActivityIndicator, Platform, StyleSheet, Text, View } from "react-native";
import { NavigationContainer, DarkTheme } from "@react-navigation/native";
import { createBottomTabNavigator } from "@react-navigation/bottom-tabs";
import { createNativeStackNavigator } from "@react-navigation/native-stack";
import { Banknote, CreditCard, History, LayoutDashboard, MoreHorizontal, PlugZap, Settings, SlidersHorizontal, SunMedium } from "lucide-react-native";
import { SafeAreaProvider, SafeAreaView, useSafeAreaInsets } from "react-native-safe-area-context";
import { useAuth } from "./AuthContext";
import { appStoreSubscriptionsEnabled } from "./releaseConfig";
import { MoreStackParamList, RootStackParamList, RootTabsParamList } from "./navigationTypes";
import { AppButton, Card, ErrorBanner, Header, Screen } from "../core/components";
import { openPublicLink, PUBLIC_PRIVACY_URL, PUBLIC_SUPPORT_URL, PUBLIC_TERMS_URL } from "../core/publicLinks";
import { colors } from "../core/theme";
import { LoginScreen } from "../features/auth/LoginScreen";
import { DashboardScreen } from "../features/dashboard/DashboardScreen";
import { InverterDetailsScreen } from "../features/dashboard/InverterDetailsScreen";
import { SolarEstimateDetailsScreen } from "../features/generation/SolarEstimateDetailsScreen";
import { SalesDetailsScreen } from "../features/sales/SalesDetailsScreen";
import { DevicesScreen } from "../features/devices/DevicesScreen";
import { HistoryScreen } from "../features/history/HistoryScreen";
import { RuleEditorScreen } from "../features/rules/RuleEditorScreen";
import { RulesScreen } from "../features/rules/RulesScreen";
import { SettingsScreen } from "../features/settings/SettingsScreen";
import { GenerationScreen } from "../features/generation/GenerationScreen";
import { SalesScreen } from "../features/sales/SalesScreen";
import { SubscriptionProvider } from "../features/subscription/SubscriptionContext";
import { SubscriptionGate, SubscriptionScreen } from "../features/subscription/SubscriptionScreen";
import type { NativeStackScreenProps } from "@react-navigation/native-stack";

const RootStack = createNativeStackNavigator<RootStackParamList>();
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
  const { t } = useLanguage();
  const { isAuthenticated, isBootstrapping, isDemo, apiBaseUrl, username, logout } = useAuth();

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

  if (isDemo) {
    return <View style={styles.demoContainer}>
      <SafeAreaView edges={["top"]} style={styles.demoBanner}>
        <View style={styles.demoCopy}>
          <Text style={styles.demoTitle}>{t("Demo · Sample data")}</Text>
          <Text style={styles.demoText}>{t("Changes stay in this session. No devices affected.")}</Text>
        </View>
        <AppButton label={t("Exit demo")} onPress={() => void logout()} variant="secondary" compact />
      </SafeAreaView>
      <SafeAreaProvider style={styles.demoContainer}><SolarNavigator key="demo" /></SafeAreaProvider>
    </View>;
  }

  if (Platform.OS !== "ios" || !appStoreSubscriptionsEnabled) return <SolarNavigator key="real" />;

  return <SubscriptionProvider key={`${apiBaseUrl}:${username}`}>
    <SubscriptionGate onLogout={logout} privacyUrl={PUBLIC_PRIVACY_URL} termsUrl={PUBLIC_TERMS_URL} supportUrl={PUBLIC_SUPPORT_URL}>
      <SolarNavigator key="real" />
    </SubscriptionGate>
  </SubscriptionProvider>;
}

function SolarNavigator() {
  return <NavigationContainer theme={navigationTheme}>
    <RootStack.Navigator screenOptions={{
      headerStyle: { backgroundColor: colors.background },
      headerTintColor: colors.text,
      headerShadowVisible: false,
      contentStyle: { backgroundColor: colors.background },
      headerBackButtonDisplayMode: "minimal",
      title: ""
    }}>
      <RootStack.Screen name="MainTabs" component={MainTabs} options={{ headerShown: false }} />
      <RootStack.Screen name="InverterDetails" component={InverterDetailsScreen} />
      <RootStack.Screen name="SolarEstimateDetails" component={SolarEstimateDetailsScreen} />
      <RootStack.Screen name="SalesDetails" component={SalesDetailsScreen} />
    </RootStack.Navigator>
  </NavigationContainer>;
}

function MainTabs() {
  const { t } = useLanguage();
  const insets = useSafeAreaInsets();
  return (
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
            tabBarLabel: t("Home"),
            tabBarButtonTestID: "tab-home",
            tabBarAccessibilityLabel: t("Home"),
            tabBarIcon: ({ color, size }) => <LayoutDashboard color={color} size={size} />
          }}
        />
        <Tab.Screen
          name="Generation"
          component={GenerationScreen}
          options={{
            tabBarLabel: t("Generation"),
            tabBarButtonTestID: "tab-generation",
            tabBarAccessibilityLabel: t("Generation"),
            tabBarIcon: ({ color, size }) => <SunMedium color={color} size={size} />
          }}
        />
        <Tab.Screen
          name="Sales"
          component={SalesScreen}
          options={{
            tabBarLabel: t("Sales"),
            tabBarButtonTestID: "tab-sales",
            tabBarAccessibilityLabel: t("Sales"),
            tabBarIcon: ({ color, size }) => <Banknote color={color} size={size} />
          }}
        />
        <Tab.Screen
          name="Devices"
          component={DevicesScreen}
          options={{
            tabBarLabel: t("Devices"),
            tabBarButtonTestID: "tab-devices",
            tabBarAccessibilityLabel: t("Devices"),
            tabBarIcon: ({ color, size }) => <PlugZap color={color} size={size} />
          }}
        />
        <Tab.Screen
          name="More"
          component={MoreStackNavigator}
          options={{
            tabBarLabel: t("More"),
            tabBarButtonTestID: "tab-more",
            tabBarAccessibilityLabel: t("More"),
            tabBarIcon: ({ color, size }) => <MoreHorizontal color={color} size={size} />
          }}
        />
      </Tab.Navigator>
  );
}

function MoreStackNavigator() {
  const { t } = useLanguage();
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
      <MoreStack.Screen name="RuleEditor" component={RuleEditorScreen} options={{ title: t("Rule") }} />
      <MoreStack.Screen name="History" component={HistoryScreen} />
      <MoreStack.Screen name="Settings" component={SettingsScreen} />
      <MoreStack.Screen name="Subscription" component={SubscriptionRoute} />
    </MoreStack.Navigator>
  );
}

function MoreScreen({ navigation }: NativeStackScreenProps<MoreStackParamList, "MoreHome">) {
  const { t } = useLanguage();
  const { username, isDemo, logout } = useAuth();
  const [linkError, setLinkError] = useState<string | null>(null);
  async function openLink(url: string) {
    setLinkError(null);
    try { await openPublicLink(url); }
    catch (error) { setLinkError(error instanceof Error ? error.message : "Unable to open the link."); }
  }
  return (
    <Screen>
      <Header title={t("More")} subtitle={isDemo ? t("Sample solar installation") : username ? t("Signed in as {0}", username) : t("Your Solar installation")} />
      <Card>
        <AppButton label={t("Automation rules")} icon={SlidersHorizontal} variant="ghost" onPress={() => navigation.navigate("RulesList")} />
        <AppButton label={t("Readings & run history")} icon={History} variant="ghost" onPress={() => navigation.navigate("History")} />
        <AppButton label={t("Settings & account")} icon={Settings} variant="ghost" onPress={() => navigation.navigate("Settings")} />
        {!isDemo && Platform.OS === "ios" && appStoreSubscriptionsEnabled && <AppButton label={t("Subscription")} icon={CreditCard} variant="ghost" onPress={() => navigation.navigate("Subscription")} />}
      </Card>
      <ErrorBanner message={linkError} />
      <AppButton label={t("Privacy policy")} variant="ghost" onPress={() => void openLink(PUBLIC_PRIVACY_URL)} />
      <AppButton label={t("Terms of use")} variant="ghost" onPress={() => void openLink(PUBLIC_TERMS_URL)} />
      <AppButton label={t("Support")} variant="ghost" onPress={() => void openLink(PUBLIC_SUPPORT_URL)} />
      <AppButton label={isDemo ? t("Exit demo") : t("Logout")} variant="secondary" onPress={() => void logout()} />
    </Screen>
  );
}

function SubscriptionRoute() {
  const { t } = useLanguage();
  const { isDemo, logout } = useAuth();
  if (isDemo || Platform.OS !== "ios" || !appStoreSubscriptionsEnabled) return <Screen><Header title="Solar Premium" subtitle={t("Subscriptions are not enabled in this TestFlight build.")} /></Screen>;
  return <SubscriptionScreen onLogout={logout} privacyUrl={PUBLIC_PRIVACY_URL} termsUrl={PUBLIC_TERMS_URL} supportUrl={PUBLIC_SUPPORT_URL} />;
}

const styles = StyleSheet.create({
  demoContainer: { flex: 1, backgroundColor: colors.background },
  demoBanner: { flexDirection: "row", alignItems: "center", gap: 8, paddingHorizontal: 12, paddingBottom: 8, backgroundColor: colors.surface },
  demoCopy: { flex: 1, gap: 4 },
  demoTitle: { color: colors.primary, fontSize: 13, fontWeight: "800" },
  demoText: { color: colors.muted, fontSize: 10, lineHeight: 14 },
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
