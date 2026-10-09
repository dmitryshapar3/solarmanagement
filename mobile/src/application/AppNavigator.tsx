import { useEffect, useMemo, useState } from "react";
import { View } from "react-native";
import { NavigationContainer, DefaultTheme, DarkTheme, type InitialState } from "@react-navigation/native";
import { createBottomTabNavigator } from "@react-navigation/bottom-tabs";
import { createNativeStackNavigator } from "@react-navigation/native-stack";
import { SafeAreaView } from "react-native-safe-area-context";
import { useAuth } from "./AuthContext";
import { useLanguage } from "./LanguageContext";
import type { AuthStackParamList, AutomationsStackParamList, HomeStackParamList, RootStackParamList, RootTabsParamList } from "./navigationTypes";
import { AppButton, Banner, LoadingState, ScreenTopInsetContext, ThemedText as Text } from "../core/components";
import { PUBLIC_PRIVACY_URL, PUBLIC_SUPPORT_URL, PUBLIC_TERMS_URL } from "../core/publicLinks";
import { useTheme } from "../ui/theme/ThemeProvider";
import { FloatingTabBar } from "./FloatingTabBar";
import { WelcomeScreen, CodeRequestScreen, EmailCodeScreen, PasswordLoginScreen, ServerScreen } from "../features/auth/AuthScreens";
import { DashboardScreen } from "../features/dashboard/DashboardScreen";
import { LiveReadingsScreen } from "../features/readings/LiveReadingsScreen";
import { ReadingsLogScreen } from "../features/readings/ReadingsLogScreen";
import { HourlyTableSheet } from "../features/generation/HourlyTableSheet";
import { ExportHourlySheet } from "../features/sales/ExportHourlySheet";
import { DevicesScreen } from "../features/devices/DevicesScreen";
import { DeviceSheetScreen } from "../features/devices/DeviceSheetScreen";
import { RuleEditorScreen } from "../features/rules/RuleEditorScreen";
import { RulesScreen } from "../features/rules/RulesScreen";
import { ActivityScreen } from "../features/activity/ActivityScreen";
import { SettingsScreen } from "../features/settings/SettingsScreen";
import { AppearanceScreen, ChangePasswordScreen, ConnectedServicesScreen, DataRefreshScreen, DeleteAccountScreen, EditProfileScreen, LanguageScreen, PasswordSessionsScreen, ServerSettingsScreen, SignInSecurityScreen, SolarSiteScreen, TariffExportScreen, TimeZoneScreen } from "../features/settings/SettingsPages";
import { ConnectScreen } from "../features/connect/ConnectScreen";
import { EnergyScreen } from "../features/energy/EnergyScreen";
import { SubscriptionProvider } from "../features/subscription/SubscriptionContext";
import { SubscriptionGate, SubscriptionScreen, DemoPaywallScreen } from "../features/subscription/SubscriptionScreen";
import { DesignGalleryScreen } from "../ui/DesignGalleryScreen";
const Root = createNativeStackNavigator<RootStackParamList>(); const Home = createNativeStackNavigator<HomeStackParamList>();
const Auth = createNativeStackNavigator<AuthStackParamList>(); const Automations = createNativeStackNavigator<AutomationsStackParamList>();
const Tab = createBottomTabNavigator<RootTabsParamList>();
const subscriptionLinks = { privacyUrl: PUBLIC_PRIVACY_URL, termsUrl: PUBLIC_TERMS_URL, supportUrl: PUBLIC_SUPPORT_URL };
export function AppNavigator() {
  const { t } = useLanguage(); const { colors } = useTheme();
  const { api, isAuthenticated, isBootstrapping, isDemo, apiBaseUrl, username, logout } = useAuth();
  const navigationMemory = useMemo<{ state?: InitialState }>(() => ({}), [api, api.sessionEpoch, apiBaseUrl, username, isAuthenticated, isDemo]);
  if (isBootstrapping) return <View style={{ flex: 1, justifyContent: "center", padding: 16, backgroundColor: colors.bg }}><Text style={{ fontFamily: "Unbounded-Bold", fontSize: 30 }}>SmartSolar</Text><LoadingState /></View>;
  if (!isAuthenticated) return <AuthNavigator />;
  if (isDemo) return <View style={{ flex: 1, backgroundColor: colors.bg }}><SafeAreaView edges={["top"]}><Banner tone="sample"><View style={{ gap: 8 }}><Text style={{ fontSize: 13, fontWeight: "600" }}>{t("You’re exploring sample data")}</Text><Text style={{ fontSize: 13, lineHeight: 18, color: colors.ink2 }}>{t("Numbers are made up. Switching is turned off.")}</Text><View style={{ flexDirection: "row", justifyContent: "flex-end", gap: 12 }}><AppButton label="Exit" onPress={() => void logout()} variant="ghost" compact /><AppButton label="Sign in" onPress={() => void logout()} variant="secondary" compact /></View></View></Banner></SafeAreaView><ScreenTopInsetContext.Provider value={false}><SolarNavigator key="demo" memory={navigationMemory} /></ScreenTopInsetContext.Provider></View>;
  return <SubscriptionProvider key={`${apiBaseUrl}:${username}`}><RealNavigation memory={navigationMemory} /></SubscriptionProvider>;
}
function RealNavigation({ memory }: { memory: { state?: InitialState } }) {
  const { logout } = useAuth(); const [accountOpen, setAccountOpen] = useState(false);
  useEffect(() => setAccountOpen(false), [memory]);
  if (accountOpen) return <AccountNavigator onClose={() => setAccountOpen(false)} />;
  return <SubscriptionGate onLogout={logout} onSettings={() => setAccountOpen(true)} {...subscriptionLinks}><SolarNavigator memory={memory} /></SubscriptionGate>;
}
function useStackOptions() { const { colors } = useTheme(); return { headerStyle: { backgroundColor: colors.bg }, headerTintColor: colors.ink, headerShadowVisible: false, contentStyle: { backgroundColor: colors.bg }, headerBackButtonDisplayMode: "minimal" as const, title: "" }; }
function useNavigationTheme() { const { colors, scheme } = useTheme(); const base = scheme === "dark" ? DarkTheme : DefaultTheme; return useMemo(() => ({ ...base, colors: { ...base.colors, background: colors.bg, card: colors.surface, border: colors.line, primary: colors.ink, text: colors.ink } }), [base, colors]); }
function AuthNavigator() { const options = useStackOptions(); return <NavigationContainer theme={useNavigationTheme()}><Auth.Navigator screenOptions={{ ...options, headerShown: false }}><Auth.Screen name="Welcome" component={WelcomeScreen} options={{ headerShown: false }} /><Auth.Screen name="CodeRequest" component={CodeRequestScreen} /><Auth.Screen name="EmailCode" component={EmailCodeScreen} /><Auth.Screen name="PasswordLogin" component={PasswordLoginScreen} /><Auth.Screen name="Server" component={ServerScreen} /></Auth.Navigator></NavigationContainer>; }
function SolarNavigator({ memory }: { memory?: { state?: InitialState } }) {
  const options = useStackOptions();
  return <NavigationContainer theme={useNavigationTheme()} initialState={memory?.state} onStateChange={state => { if (memory) memory.state = state; }}><Root.Navigator screenOptions={options}>
    <Root.Screen name="MainTabs" component={MainTabs} options={{ headerShown: false }} />
    <Root.Screen name="AutomationEditor" component={RuleEditorScreen as any} />
    <Root.Screen name="DeviceSheet" component={DeviceSheetScreen} options={{ headerShown: false, presentation: "formSheet", sheetCornerRadius: 38, sheetGrabberVisible: true, sheetAllowedDetents: [0.8, 1] }} />
    <Root.Screen name="ProductionHourlySheet" component={HourlyTableSheet} options={{ presentation: "formSheet", sheetCornerRadius: 38, sheetGrabberVisible: true }} />
    <Root.Screen name="ExportHourlySheet" component={ExportHourlySheet} options={{ presentation: "formSheet", sheetCornerRadius: 38, sheetGrabberVisible: true }} />
    <Root.Screen name="Connect" component={ConnectScreen} options={{ presentation: "modal" }} />
    <Root.Screen name="Paywall" component={PaywallRoute} options={{ presentation: "modal" }} />
    <Root.Screen name="AccountSettings" component={AccountStack} options={{ headerShown: false }} />
    {__DEV__ ? <Root.Screen name="DesignGallery" component={DesignGalleryScreen} /> : null}
  </Root.Navigator></NavigationContainer>;
}
function SettingsEntries() { return <>
  <Home.Screen name="Settings" component={SettingsScreen} /><Home.Screen name="SolarSite" component={SolarSiteScreen} /><Home.Screen name="TariffExport" component={TariffExportScreen} /><Home.Screen name="ConnectedServices" component={ConnectedServicesScreen} /><Home.Screen name="DataRefresh" component={DataRefreshScreen} /><Home.Screen name="Language" component={LanguageScreen} /><Home.Screen name="TimeZone" component={TimeZoneScreen} /><Home.Screen name="Appearance" component={AppearanceScreen} /><Home.Screen name="SignInSecurity" component={SignInSecurityScreen} /><Home.Screen name="PasswordSessions" component={PasswordSessionsScreen} /><Home.Screen name="ChangePassword" component={ChangePasswordScreen} /><Home.Screen name="DeleteAccount" component={DeleteAccountScreen} /><Home.Screen name="EditProfile" component={EditProfileScreen} /><Home.Screen name="Server" component={ServerSettingsScreen} />
</>; }
function HomeStack() { return <Home.Navigator screenOptions={useStackOptions()}><Home.Screen name="Home" component={DashboardScreen} options={{ headerShown: false }} /><Home.Screen name="LiveReadings" component={LiveReadingsScreen} /><Home.Screen name="ReadingsLog" component={ReadingsLogScreen} />{SettingsEntries()}</Home.Navigator>; }
function AccountStack() { return <Home.Navigator initialRouteName="Settings" screenOptions={useStackOptions()}>{SettingsEntries()}</Home.Navigator>; }
function AccountNavigator({ onClose }: { onClose: () => void }) { const { t } = useLanguage(); return <NavigationContainer theme={useNavigationTheme()}><Root.Navigator screenOptions={useStackOptions()}><Root.Screen name="AccountSettings" component={AccountStack} options={{ headerShown: true, headerLeft: () => <AppButton label={t("Close")} variant="ghost" compact onPress={onClose} /> }} /><Root.Screen name="Paywall" component={PaywallRoute} /></Root.Navigator></NavigationContainer>; }
function AutomationsStack() { return <Automations.Navigator screenOptions={useStackOptions()}><Automations.Screen name="AutomationsList" component={RulesScreen as any} options={{ headerShown: false }} /><Automations.Screen name="Activity" component={ActivityScreen} options={{ headerShown: false }} /><Automations.Screen name="ReadingsLog" component={ReadingsLogScreen} options={{ headerShown: false }} /></Automations.Navigator>; }
function MainTabs() {
  const { t } = useLanguage();
  return <Tab.Navigator tabBar={props => <FloatingTabBar {...props} />} screenOptions={{ headerShown: false }}>
    <Tab.Screen name="HomeTab" component={HomeStack} options={{ tabBarLabel: t("Home"), tabBarButtonTestID: "tab-home" }} />
    <Tab.Screen name="Energy" component={EnergyScreen} options={{ tabBarLabel: t("Energy"), tabBarButtonTestID: "tab-energy" }} />
    <Tab.Screen name="Devices" component={DevicesScreen} options={{ tabBarLabel: t("Devices"), tabBarButtonTestID: "tab-devices" }} />
    <Tab.Screen name="Automations" component={AutomationsStack} options={{ tabBarLabel: t("Automations"), tabBarButtonTestID: "tab-automations" }} />
  </Tab.Navigator>;
}
function PaywallRoute() { const { logout, isDemo } = useAuth(); return isDemo ? <DemoPaywallScreen /> : <SubscriptionScreen onLogout={logout} {...subscriptionLinks} />; }
