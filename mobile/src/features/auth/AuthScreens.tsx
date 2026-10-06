import { useEffect, useMemo, useRef, useState } from "react";
import { Keyboard, Platform, Pressable, TextInput, View } from "react-native";
import { useNavigation, useRoute, type RouteProp } from "@react-navigation/native";
import type { NativeStackNavigationProp } from "@react-navigation/native-stack";
import { ArrowLeft, Apple, Mail, Phone, Sun } from "lucide-react-native";
import { fetch as expoFetch } from "expo/fetch";
import * as Clipboard from "expo-clipboard";
import * as AppleAuthentication from "expo-apple-authentication";
import { AppButton, ErrorBanner, Header, IconButton, Screen, SegmentedControl, TextField, ThemedText as Text } from "../../core/components";
import { useAuth } from "../../application/AuthContext";
import { useLanguage } from "../../application/LanguageContext";
import type { AuthStackParamList } from "../../application/navigationTypes";
import { ApiClient } from "../../core/api/ApiClient";
import { DeyeSolarApi } from "../../core/api/DeyeSolarApi";
import type { AuthOptions, VerificationChannel } from "../../core/api/types";
import { useTheme } from "../../ui/theme/ThemeProvider";
import { googleSignIn } from "./googleSignIn";
import { GoogleSignInButton } from "./GoogleSignInButton";
import { appleAvailable } from "./appleSignIn";
import { openPublicLink, PUBLIC_PRIVACY_URL } from "../../core/publicLinks";

type Navigation = NativeStackNavigationProp<AuthStackParamList>;
type ProviderOptions = AuthOptions & { appleEnabled?: boolean; appleWebEnabled?: boolean };
function useAnonymous() {
  const { apiBaseUrl } = useAuth();
  const api = useMemo(() => new DeyeSolarApi(new ApiClient({ baseUrl: apiBaseUrl, transport: expoFetch })), [apiBaseUrl]);
  const [options, setOptions] = useState<ProviderOptions | null>(null); const [error, setError] = useState<string | null>(null);
  useEffect(() => { const controller = new AbortController(); setOptions(null); setError(null);
    void api.request<ProviderOptions>("/api/auth/options", { signal: controller.signal, skipUnauthorizedHandler: true }).then(value => { if (!controller.signal.aborted) setOptions(value); }).catch(e => { if (!controller.signal.aborted) setError(e.message); });
    return () => controller.abort(); }, [api]);
  return { api, options, error, apiBaseUrl };
}
function Back() { const navigation = useNavigation<Navigation>(); return <IconButton icon={ArrowLeft} accessibilityLabel="Back" onPress={() => navigation.goBack()} />; }

export function WelcomeScreen() {
  const navigation = useNavigation<Navigation>(); const { colors } = useTheme(); const { t } = useLanguage();
  const { apiBaseUrl, finishSignIn, signInWithApple, enterDemo, authError } = useAuth();
  const { options, error: providerError } = useAnonymous(); const [nativeApple, setNativeApple] = useState(false);
  const [busy, setBusy] = useState<string | null>(null); const [error, setError] = useState<string | null>(null);
  const mounted = useRef(true); const gate = useRef(false);
  useEffect(() => { mounted.current = true; void appleAvailable().then(value => { if (mounted.current) setNativeApple(value); }); return () => { mounted.current = false; }; }, []);
  async function run(label: string, action: () => Promise<void>) { if (gate.current) return; gate.current = true; setBusy(label); setError(null);
    try { await action(); } catch (e) { if (mounted.current) setError(e instanceof Error ? e.message : "Sign-in failed."); } finally { gate.current = false; if (mounted.current) setBusy(null); } }
  return <Screen style={{ gap: 20 }}>
    <View style={{ alignItems: "center", paddingTop: 36, paddingBottom: 16, gap: 14 }}>
      <View style={{ width: 148, height: 148, borderRadius: 74, backgroundColor: colors.sun, alignItems: "center", justifyContent: "center" }}><Sun size={86} color={colors.onSun} strokeWidth={1.5} /></View>
      <Text style={{ fontSize: 34, lineHeight: 40, fontWeight: "700", letterSpacing: -.7 }}>SmartSolar</Text>
      <Text style={{ color: colors.ink2, fontSize: 19, lineHeight: 25, textAlign: "center", maxWidth: 300 }}>{t("Let your solar do more.")}</Text>
      <Text style={{ color: colors.ink3, textAlign: "center", maxWidth: 300 }}>{t("See your energy. Automate your home. Make the most of the sun.")}</Text>
    </View>
    <ErrorBanner message={error ?? authError ?? providerError} />
    {nativeApple && options?.appleEnabled ? <View pointerEvents={busy ? "none" : "auto"} style={{ opacity: busy ? .5 : 1 }}><AppleAuthentication.AppleAuthenticationButton buttonType={AppleAuthentication.AppleAuthenticationButtonType.CONTINUE} buttonStyle={colors.ink === "#12120F" ? AppleAuthentication.AppleAuthenticationButtonStyle.BLACK : AppleAuthentication.AppleAuthenticationButtonStyle.WHITE} cornerRadius={12} style={{ width: "100%", height: 52 }} onPress={() => void run("apple", () => signInWithApple(apiBaseUrl))} /></View> : null}
    <View style={{ alignItems: "center" }}><GoogleSignInButton disabled={Boolean(busy) || !options?.googleEnabled} loading={busy === "google"} onPress={() => void run("google", async () => {
      const result = await googleSignIn(apiBaseUrl); if (result) await finishSignIn(apiBaseUrl, (api, signal) => api.exchangeGoogleCode(result.code, result.codeVerifier, signal));
    })} /></View>
    <AppButton label="Continue with email" icon={Mail} variant="secondary" disabled={Boolean(busy) || !options?.emailEnabled} onPress={() => navigation.navigate("CodeRequest", { channel: "email" })} />
    {options?.phoneEnabled ? <AppButton label="Continue with phone" icon={Phone} variant="secondary" disabled={Boolean(busy)} onPress={() => navigation.navigate("CodeRequest", { channel: "phone" })} /> : null}
    <AppButton label="Use a password instead" variant="ghost" disabled={Boolean(busy)} onPress={() => navigation.navigate("PasswordLogin")} />
    <AppButton label="Explore with sample data" variant="quiet" disabled={Boolean(busy)} loading={busy === "demo"} onPress={() => void run("demo", enterDemo)} />
    <Pressable accessibilityRole="button" accessibilityLabel={t("Change server")} disabled={Boolean(busy)} onPress={() => navigation.navigate("Server")}><Text style={{ fontSize: 13, color: colors.ink3, textAlign: "center" }}>{t("Own SmartSolar server? Change server")}</Text></Pressable>
    <AppButton label="Privacy policy" compact variant="ghost" onPress={() => void openPublicLink(PUBLIC_PRIVACY_URL).catch(e => setError(e.message))} />
  </Screen>;
}

export function CodeRequestScreen() {
  const navigation = useNavigation<Navigation>(); const route = useRoute<RouteProp<AuthStackParamList, "CodeRequest">>();
  const { api, options, error: providerError } = useAnonymous(); const { t } = useLanguage(); const { colors } = useTheme();
  const [channel, setChannel] = useState<VerificationChannel>(route.params?.channel ?? "email"); const [destination, setDestination] = useState("");
  const [busy, setBusy] = useState(false); const [error, setError] = useState<string | null>(null); const gate = useRef(false); const mounted = useRef(true);
  useEffect(() => () => { mounted.current = false; }, []);
  async function send() { if (gate.current || !destination.trim() || !(channel === "email" ? options?.emailEnabled : options?.phoneEnabled)) return; gate.current = true; setBusy(true); setError(null); Keyboard.dismiss();
    try { const result = await api.startCodeSignIn(channel, destination.trim()); if (mounted.current) navigation.navigate("EmailCode", { channel, destination: destination.trim(), ...result }); }
    catch (e) { if (mounted.current) setError(e instanceof Error ? e.message : "Unable to send the code."); } finally { gate.current = false; if (mounted.current) setBusy(false); } }
  return <Screen><Back /><Header title={channel === "email" ? "What's your email?" : "What's your phone number?"} subtitle="We'll send a code to sign in or create your account." />
    {options?.emailEnabled && options.phoneEnabled ? <SegmentedControl options={[{ label: "Email", value: "email" }, { label: "Phone", value: "phone" }]} value={channel} onChange={value => { if (!busy) { setChannel(value); setDestination(""); } }} /> : null}
    <TextField label={channel === "email" ? "Email address" : "Phone number"} value={destination} onChangeText={setDestination} keyboardType={channel === "email" ? "email-address" : "phone-pad"} autoComplete={channel === "email" ? "email" : "tel"} editable={!busy} onSubmitEditing={() => void send()} returnKeyType="go" helper={channel === "phone" ? "Include the country code, for example +48." : undefined} />
    <ErrorBanner message={error ?? providerError} /><AppButton label="Send code" loading={busy} disabled={!destination.trim() || !(channel === "email" ? options?.emailEnabled : options?.phoneEnabled)} onPress={() => void send()} />
    <Text style={{ color: colors.ink3, fontSize: 13 }}>{t("Your installation and trial stay with your account.")}</Text>
    <AppButton label="Use a password instead" variant="ghost" disabled={busy} onPress={() => navigation.navigate("PasswordLogin")} />
  </Screen>;
}

export function EmailCodeScreen() {
  const navigation = useNavigation<Navigation>(); const route = useRoute<RouteProp<AuthStackParamList, "EmailCode">>(); const { api } = useAnonymous();
  const { finishSignIn, apiBaseUrl } = useAuth(); const { colors } = useTheme(); const { t } = useLanguage(); const params = route.params;
  const initialServer = useRef(apiBaseUrl); const [challenge, setChallenge] = useState({ verificationId: params.verificationId, expiresAt: params.expiresAt });
  const [resendAt, setResendAt] = useState(Date.now() + params.retryAfterSeconds * 1000); const [now, setNow] = useState(Date.now()); const [code, setCode] = useState("");
  const [busy, setBusy] = useState<string | null>(null); const [error, setError] = useState<string | null>(null); const gate = useRef(false); const mounted = useRef(true); const input = useRef<TextInput>(null);
  useEffect(() => { const timer = setInterval(() => setNow(Date.now()), 1000); return () => { mounted.current = false; clearInterval(timer); }; }, []);
  async function run(label: string, action: () => Promise<void>) { if (gate.current) return; gate.current = true; setBusy(label); setError(null);
    try { if (initialServer.current !== apiBaseUrl) throw new Error("Server changed. Request a new code."); await action(); } catch (e) { if (mounted.current) setError(e instanceof Error ? e.message : "Verification failed."); } finally { gate.current = false; if (mounted.current) setBusy(null); } }
  const remaining = Math.max(0, Math.ceil((resendAt - now) / 1000)); const expired = now >= Date.parse(challenge.expiresAt);
  return <Screen><Back /><Header title="Check your messages" subtitle={t("Enter the 6-digit code sent to {0}.", params.destination)} />
    <Pressable accessibilityRole="button" accessibilityLabel={t("Enter verification code")} onPress={() => input.current?.focus()} style={{ flexDirection: "row", gap: 8 }}>
      {Array.from({ length: 6 }, (_, i) => <View key={i} style={{ flex: 1, minHeight: 62, borderRadius: 12, borderWidth: 1, borderColor: code.length === i ? colors.ink : colors.line, alignItems: "center", justifyContent: "center", backgroundColor: colors.surface }}><Text style={{ fontSize: 28, fontWeight: "600" }}>{code[i] ?? ""}</Text></View>)}
    </Pressable>
    <TextInput ref={input} autoFocus accessibilityLabel={t("Verification code")} value={code} onChangeText={value => setCode(value.replace(/\D/g, "").slice(0, 6))} keyboardType="number-pad" textContentType="oneTimeCode" autoComplete={Platform.OS === "android" ? "sms-otp" : "one-time-code"} maxLength={6} editable={!busy} style={{ height: 44, color: colors.ink, fontSize: 17, textAlign: "center" }} placeholder={t("Paste or enter your code")} placeholderTextColor={colors.ink3} />
    <ErrorBanner message={error ?? (expired ? t("The code has expired. Request a new code.") : null)} />
    <AppButton label="Continue" disabled={Boolean(busy) || code.length !== 6 || expired} loading={busy === "verify"} onPress={() => void run("verify", () => finishSignIn(apiBaseUrl, (next, signal) => next.completeCodeSignIn(challenge.verificationId, code, signal)))} />
    <AppButton label="Paste code" variant="ghost" disabled={Boolean(busy)} onPress={() => void Clipboard.getStringAsync().then(value => { if (mounted.current) setCode(value.replace(/\D/g, "").slice(0, 6)); }).catch(e => setError(e.message))} />
    <AppButton label={remaining ? t("Resend in {0}s", remaining) : t("Resend code")} variant="ghost" disabled={Boolean(busy) || remaining > 0} loading={busy === "resend"} onPress={() => void run("resend", async () => { const next = await api.startCodeSignIn(params.channel, params.destination); if (mounted.current) { setChallenge(next); setCode(""); setResendAt(Date.now() + next.retryAfterSeconds * 1000); } })} />
    <AppButton label={params.channel === "email" ? "Change email" : "Change phone"} variant="ghost" disabled={Boolean(busy)} onPress={() => navigation.replace("CodeRequest", { channel: params.channel })} />
    <AppButton label="Use a password instead" variant="ghost" disabled={Boolean(busy)} onPress={() => navigation.navigate("PasswordLogin")} />
  </Screen>;
}

export function PasswordLoginScreen() {
  const navigation = useNavigation<Navigation>(); const { login, apiBaseUrl } = useAuth(); const [username, setUsername] = useState(""); const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false); const [error, setError] = useState<string | null>(null); const gate = useRef(false); const mounted = useRef(true); useEffect(() => () => { mounted.current = false; }, []);
  async function submit() { if (gate.current) return; gate.current = true; setBusy(true); setError(null); Keyboard.dismiss(); try { await login({ baseUrl: apiBaseUrl, username, password }); } catch (e) { if (mounted.current) setError(e instanceof Error ? e.message : "Unable to sign in."); } finally { gate.current = false; if (mounted.current) { setBusy(false); setPassword(""); } } }
  return <Screen><Back /><Header title="Sign in with a password" /><TextField label="Username, email or phone" value={username} onChangeText={setUsername} editable={!busy} autoComplete="username" /><TextField label="Password" value={password} onChangeText={setPassword} secureTextEntry editable={!busy} maxLength={128} autoComplete="current-password" onSubmitEditing={() => void submit()} returnKeyType="go" /><ErrorBanner message={error} /><AppButton label="Sign in" disabled={!username.trim() || !password} loading={busy} onPress={() => void submit()} /><AppButton label="Sign in with code" variant="ghost" disabled={busy} onPress={() => navigation.navigate("CodeRequest", { channel: "email" })} /></Screen>;
}

export function ServerScreen() {
  const { apiBaseUrl, updateApiBaseUrl } = useAuth(); const navigation = useNavigation<Navigation>(); const [url, setUrl] = useState(apiBaseUrl); const [busy, setBusy] = useState(false); const [error, setError] = useState<string | null>(null);
  return <Screen><Back /><Header title="Server" subtitle="Connect to your own SmartSolar server." /><TextField label="Server URL" value={url} onChangeText={setUrl} editable={!busy} keyboardType="url" helper="Use an HTTPS address. Your account belongs to this server." /><ErrorBanner message={error} /><AppButton label="Save server" loading={busy} onPress={() => { setBusy(true); setError(null); void updateApiBaseUrl(url).then(() => { if (navigation.canGoBack()) navigation.goBack(); }).catch(e => setError(e.message)).finally(() => setBusy(false)); }} /></Screen>;
}
