import { useCallback, useEffect, useMemo, useState } from "react";
import { View } from "react-native";
import { useNavigation, type NavigationProp } from "@react-navigation/native";
import { CircleHelp, Globe, KeyRound, MapPin, Plug, RefreshCw, Settings2, ShieldCheck, Sun, User } from "lucide-react-native";
import { useAuth } from "../../application/AuthContext";
import { useLanguage } from "../../application/LanguageContext";
import type { HomeStackParamList, RootStackParamList } from "../../application/navigationTypes";
import { AppButton, Card, ErrorBanner, Group, Header, LoadingState, NavigationRow, Screen, SectionTitle, ThemedText as Text } from "../../core/components";
import { useTheme } from "../../ui/theme/ThemeProvider";
import { useScopedAction } from "../../application/useScopedAction";
import { accountApi, type AccountProfile } from "../auth/accountApi";
import { useOptionalSubscription } from "../subscription/SubscriptionContext";
import { openPublicLink, PUBLIC_SUPPORT_URL, PUBLIC_PRIVACY_URL } from "../../core/publicLinks";

export function SettingsScreen() {
  const navigation = useNavigation<NavigationProp<HomeStackParamList & RootStackParamList>>(); const { api, apiBaseUrl, username, isDemo, logout } = useAuth();
  const { colors, appearance } = useTheme(); const { t, language, languages } = useLanguage(); const subscription = useOptionalSubscription();
  const account = useMemo(() => accountApi(api), [api]); const [profile, setProfile] = useState<AccountProfile | null>(null); const [loading, setLoading] = useState(true); const [error, setError] = useState<string | null>(null);
  const actions = useScopedAction(api, apiBaseUrl, () => { setProfile(null); setLoading(true); setError(null); });
  const load = useCallback(() => actions.run("load", async context => {
    const next = isDemo ? { displayName: "Alex", verifiedEmail: null, verifiedPhone: null } : await account.profile(context.signal);
    context.publish(() => { setProfile(next); setLoading(false); });
  }, { failed: e => { setError(e instanceof Error ? e.message : "Account could not be loaded."); setLoading(false); } }), [account, actions.run, isDemo]);
  useEffect(() => { void load(); }, [load]);
  const name = profile?.displayName || username?.split("@")[0] || t("Your account"); const initials = name.split(/\s+/).slice(0, 2).map(part => part[0] ?? "").join("").toUpperCase();
  const access = subscription?.access; const trialDays = access ? Math.max(0, Math.ceil((Date.parse(access.trialEndsAt) - Date.parse(access.serverNow)) / 86400000)) : null;
  function paywall() { navigation.navigate("Paywall"); }
  return <Screen refreshing={loading || Boolean(actions.busy)} onRefresh={load}><Header title="Settings" />
    <ErrorBanner message={error} />{loading ? <LoadingState /> : null}
    <Group><NavigationRow title={name} subtitle={profile?.verifiedEmail ?? (isDemo ? t("Sample account") : username ?? undefined)} icon={User} onPress={() => navigation.navigate("EditProfile")} value={initials} /></Group>
    <Card style={{ backgroundColor: colors.sunTint, gap: 10 }}><View style={{ flexDirection: "row", alignItems: "center", gap: 10 }}><Sun color={colors.solar} size={24} /><Text style={{ flex: 1, fontSize: 19, fontWeight: "700" }}>{t(access?.status === "active" ? "SmartSolar Premium" : access?.status === "expired" ? "Your trial has ended" : access?.status === "trial" ? "SmartSolar trial" : "Account access")}</Text></View>
      <Text style={{ color: colors.ink2 }}>{isDemo ? t("Sample data. Explore every screen.") : access?.status === "active" ? t("Your subscription is verified by the server.") : trialDays !== null ? t("{0} days remaining · one smart plug", trialDays) : t("Account access is being checked.")}</Text>
      <AppButton label={access?.status === "active" ? "Manage subscription" : "See plans"} variant="quiet" onPress={() => access?.status === "active" ? void subscription?.manage() : paywall()} /></Card>
    <SectionTitle title="Installation" /><Group>
      <NavigationRow title="Solar site" icon={MapPin} onPress={() => navigation.navigate("SolarSite")} />
      <NavigationRow title="Tariff & export" icon={Sun} onPress={() => navigation.navigate("TariffExport")} />
      <NavigationRow title="Connected services" icon={Plug} onPress={() => navigation.navigate("ConnectedServices")} />
      <NavigationRow title="Data refresh" icon={RefreshCw} onPress={() => navigation.navigate("DataRefresh")} />
    </Group>
    <SectionTitle title="Preferences" /><Group>
      <NavigationRow title="Language" icon={Globe} value={languages.find(item => item.code === language)?.name} onPress={() => navigation.navigate("Language")} />
      <NavigationRow title="Time zone" icon={Globe} onPress={() => navigation.navigate("TimeZone")} />
      <NavigationRow title="Appearance" icon={Sun} value={t(appearance === "light" ? "Light" : appearance === "dark" ? "Dark" : "System")} onPress={() => navigation.navigate("Appearance")} />
    </Group>
    <SectionTitle title="Account" /><Group>
      <NavigationRow title="Sign-in & security" icon={ShieldCheck} onPress={() => navigation.navigate("SignInSecurity")} />
      <NavigationRow title="Password & sessions" icon={KeyRound} onPress={() => navigation.navigate("PasswordSessions")} />
      <NavigationRow title="Privacy policy" icon={CircleHelp} onPress={() => void openPublicLink(PUBLIC_PRIVACY_URL).catch(e => setError(e.message))} />
      <NavigationRow title="Support" icon={CircleHelp} onPress={() => void openPublicLink(PUBLIC_SUPPORT_URL).catch(e => setError(e.message))} />
    </Group><SectionTitle title="Advanced" /><Group><NavigationRow title="Server" icon={Settings2} value={apiBaseUrl.replace(/^https?:\/\//, "")} onPress={() => navigation.navigate("Server")} /></Group>
    <AppButton label={isDemo ? "Exit demo" : "Sign out"} variant="critical" onPress={() => void logout()} />
  </Screen>;
}
