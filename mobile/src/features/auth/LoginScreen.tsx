import { useLanguage } from "../../application/LanguageContext";
import { useEffect, useMemo, useRef, useState } from "react";
import { fetch as expoFetch } from "expo/fetch";
import { Keyboard, StyleSheet, Text, View } from "react-native";
import { LogIn, Server } from "lucide-react-native";
import { AppButton, Card, ErrorBanner, Screen, TextField } from "../../core/components";
import { colors, spacing, typography } from "../../core/theme";
import { useAuth } from "../../application/AuthContext";
import { openPublicLink, PUBLIC_PRIVACY_URL, PUBLIC_SUPPORT_URL } from "../../core/publicLinks";
import { ApiClient } from "../../core/api/ApiClient";
import { DeyeSolarApi } from "../../core/api/DeyeSolarApi";
import type { AuthOptions, VerificationChannel, VerificationResponse } from "../../core/api/types";
import { googleSignIn } from "./googleSignIn";
import { VerificationRequests } from "./identityOperations";

export function LoginScreen() {
  const { t } = useLanguage();
  const { apiBaseUrl, authError, login, finishSignIn, enterDemo } = useAuth();
  const [baseUrl, setBaseUrl] = useState(apiBaseUrl);
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState<"login" | "demo" | "google" | "send" | null>(null);
  const [mode, setMode] = useState<"password" | "register" | "code">("password");
  const [channel, setChannel] = useState<VerificationChannel>("email");
  const [destination, setDestination] = useState("");
  const [code, setCode] = useState("");
  const [verification, setVerification] = useState<VerificationResponse | null>(null);
  const [resendAt, setResendAt] = useState(0);
  const [now, setNow] = useState(Date.now());
  const [options, setOptions] = useState<AuthOptions | null>(null);
  const [verificationRequests] = useState(() => new VerificationRequests());
  const anonymousApi = useMemo(() => {
    try { return new DeyeSolarApi(new ApiClient({ baseUrl, transport: expoFetch })); }
    catch { return null; }
  }, [baseUrl]);
  const mounted = useRef(true);
  const submitting = useRef(false);

  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; };
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    verificationRequests.replace(anonymousApi);
    setOptions(null); setVerification(null); setCode("");
    if (anonymousApi) void anonymousApi.getAuthOptions(controller.signal).then(value => {
      if (!controller.signal.aborted) {
        setOptions(value);
        setChannel(value.emailEnabled ? "email" : "phone");
      }
    }).catch(() => {});
    return () => { controller.abort(); verificationRequests.cancel(); };
  }, [anonymousApi, verificationRequests]);

  useEffect(() => {
    if (!verification) return;
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [verification]);

  function changeMode(value: typeof mode) {
    if (submitting.current) return;
    setMode(value); setVerification(null); setCode(""); setPassword(""); setError(null);
  }

  function changeBaseUrl(value: string) {
    if (value === baseUrl) return;
    verificationRequests.cancel();
    setVerification(null); setCode(""); setPassword(""); setOptions(null); setError(null); setResendAt(0);
    setBaseUrl(value);
  }

  async function handleLogin() {
    if (submitting.current) return;
    Keyboard.dismiss();
    submitting.current = true;
    setError(null);
    setPending("login");
    try {
      if (mode === "password") await login({ baseUrl, username, password });
      else {
        if (!verification) throw new Error(t("Request a verification code first."));
        if (Date.now() >= Date.parse(verification.expiresAt)) throw new Error(t("The code has expired. Request a new code."));
        if (!code.trim()) throw new Error(t("Enter the verification code."));
        if (mode === "register" && password.length < 12) throw new Error(t("Choose a password with at least 12 characters."));
        await finishSignIn(baseUrl, (api, signal) => mode === "register"
          ? api.register(verification.verificationId, code.trim(), password, signal)
          : api.loginWithVerification(verification.verificationId, code.trim(), signal));
      }
    } catch (ex) {
      if (mounted.current) setError(ex instanceof Error ? ex.message : "Unable to sign in.");
    } finally {
      submitting.current = false;
      if (mounted.current) {
        setPassword("");
        setPending(null);
      }
    }
  }

  async function sendCode() {
    if (submitting.current || !anonymousApi) return;
    if (!destination.trim()) { setError("Enter your email address or phone number."); return; }
    submitting.current = true; setPending("send"); setError(null); Keyboard.dismiss();
    try {
      const response = await verificationRequests.start(channel, destination.trim(), mode === "register" ? "register" : "login");
      if (mounted.current) {
        setVerification(response); setCode(""); setNow(Date.now());
        setResendAt(Date.now() + response.retryAfterSeconds * 1000);
      }
    } catch (ex) {
      if (mounted.current && !(ex instanceof Error && ex.name === "AbortError"))
        setError(ex instanceof Error ? ex.message : "Unable to send the code.");
    }
    finally { submitting.current = false; if (mounted.current) setPending(null); }
  }

  async function handleGoogle() {
    if (submitting.current) return;
    submitting.current = true; setPending("google"); setError(null); Keyboard.dismiss();
    try {
      const response = await googleSignIn(baseUrl);
      if (response && mounted.current) await finishSignIn(baseUrl, (api, signal) => api.exchangeGoogleCode(response.code, response.codeVerifier, signal));
    } catch (ex) { if (mounted.current) setError(ex instanceof Error ? ex.message : "Unable to sign in with Google."); }
    finally { submitting.current = false; if (mounted.current) setPending(null); }
  }

  async function handleDemo() {
    if (submitting.current) return;
    Keyboard.dismiss();
    submitting.current = true;
    setError(null);
    setPending("demo");
    try {
      await enterDemo();
    } catch (ex) {
      if (mounted.current) setError(ex instanceof Error ? ex.message : "Unable to open the demo.");
    } finally {
      submitting.current = false;
      if (mounted.current) {
        setPassword("");
        setPending(null);
      }
    }
  }

  async function openLink(url: string) {
    try {
      await openPublicLink(url);
    } catch (ex) {
      if (mounted.current) setError(ex instanceof Error ? ex.message : "The link could not be opened.");
    }
  }

  return (
    <Screen>
      <View style={styles.brand}>
        <View style={styles.brandIcon}>
          <Server color={colors.primary} size={28} strokeWidth={2.2} />
        </View>
        <Text style={styles.title}>DeyeSolar</Text>
        <Text style={styles.subtitle}>{t("Solar Energy Management")}</Text>
      </View>

      <Card style={styles.form}>
        <TextField label={t("API URL")} value={baseUrl} onChangeText={changeBaseUrl} editable={!pending} placeholder="https://solar.dshapar.com" />
        {baseUrl.trim().toLowerCase().startsWith("http:") && (
          <Text style={styles.warning}>{t("HTTP is unencrypted. Use it only for a trusted local development server.")}</Text>
        )}
        <View style={styles.links}>
          <AppButton label={t("Password")} compact variant={mode === "password" ? "primary" : "ghost"} disabled={Boolean(pending)} onPress={() => changeMode("password")} />
          <AppButton label={t("Sign in with code")} compact variant={mode === "code" ? "primary" : "ghost"} disabled={Boolean(pending) || !(options?.emailEnabled || options?.phoneEnabled)} onPress={() => changeMode("code")} />
          <AppButton label={t("Create account")} compact variant={mode === "register" ? "primary" : "ghost"} disabled={Boolean(pending) || !options?.registrationEnabled || !(options.emailEnabled || options.phoneEnabled)} onPress={() => changeMode("register")} />
        </View>
        {mode === "password" ? <TextField label={t("Username, email or phone")} value={username} onChangeText={setUsername} placeholder={t("Email or username")} /> : <>
          <View style={styles.links}>
            <AppButton label={t("Email")} compact variant={channel === "email" ? "primary" : "ghost"} disabled={Boolean(pending) || !options?.emailEnabled || Boolean(verification)} onPress={() => { setChannel("email"); setDestination(""); }} />
            <AppButton label={t("Phone")} compact variant={channel === "phone" ? "primary" : "ghost"} disabled={Boolean(pending) || !options?.phoneEnabled || Boolean(verification)} onPress={() => { setChannel("phone"); setDestination(""); }} />
          </View>
          <TextField label={channel === "email" ? t("Email address") : t("Phone number")} value={destination} onChangeText={setDestination}
            keyboardType={channel === "email" ? "email-address" : "phone-pad"} placeholder={channel === "email" ? "you@example.com" : "+48123456789"} editable={!verification && !pending} />
          {verification ? <>
            <Text style={styles.warning}>{t("If this address or number is eligible, a code has been sent. Enter it below.")}</Text>
            <TextField label={t("Verification code")} value={code} onChangeText={value => setCode(value.replace(/[^0-9]/g, "").slice(0, 8))} keyboardType="number-pad" />
            <AppButton label={now < resendAt ? t("Resend in {0}s", Math.ceil((resendAt - now) / 1000)) : t("Resend code")} variant="ghost" onPress={() => void sendCode()} disabled={Boolean(pending) || now < resendAt} />
            <AppButton label={t("Change email or phone")} variant="ghost" onPress={() => { setVerification(null); setCode(""); }} disabled={Boolean(pending)} />
          </> : <AppButton label={t("Send verification code")} variant="secondary" onPress={() => void sendCode()} loading={pending === "send"} disabled={Boolean(pending)} />}
        </>}
        {mode !== "code" && <TextField
          label={t("Password")}
          value={password}
          onChangeText={setPassword}
          secureTextEntry
          placeholder={mode === "register" ? t("At least 12 characters") : t("Password")}
          returnKeyType="go"
          onSubmitEditing={handleLogin}
        />}
        <ErrorBanner message={error ?? authError} />
        <AppButton label={mode === "register" ? t("Create account") : t("Sign in")} icon={LogIn} onPress={handleLogin} loading={pending === "login"} disabled={Boolean(pending) || (mode !== "password" && !verification)} />
        <AppButton label={t("Continue with Google")} variant="secondary" onPress={() => void handleGoogle()} loading={pending === "google"} disabled={Boolean(pending) || !options?.googleEnabled} />
        {options?.googleEnabled ? <Text style={styles.warning}>{t("To connect Google to an existing account, sign in first and link it in Settings.")}</Text> : <Text style={styles.warning}>{t("Google sign-in and registration are available when enabled by your Solar server.")}</Text>}
        <AppButton label={t("Try demo")} onPress={handleDemo} variant="secondary" loading={pending === "demo"} disabled={Boolean(pending)} />
        <Text style={styles.warning}>{t("Explore sample data. Demo changes stay in this session and never affect real devices.")}</Text>
      </Card>
      <View style={styles.links}>
        <AppButton label={t("Privacy policy")} variant="ghost" compact onPress={() => void openLink(PUBLIC_PRIVACY_URL)} />
        <AppButton label={t("Support")} variant="ghost" compact onPress={() => void openLink(PUBLIC_SUPPORT_URL)} />
      </View>
    </Screen>
  );
}

const styles = StyleSheet.create({
  brand: {
    paddingTop: spacing.xxl,
    gap: spacing.md
  },
  brandIcon: {
    width: 54,
    height: 54,
    borderRadius: 8,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: colors.surfaceRaised,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: colors.border
  },
  title: {
    color: colors.text,
    fontSize: 34,
    fontWeight: "800"
  },
  subtitle: {
    color: colors.muted,
    fontSize: typography.body,
    lineHeight: 22
  },
  form: {
    gap: spacing.lg
  },
  links: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: spacing.md
  },
  warning: {
    color: colors.muted,
    fontSize: typography.caption,
    lineHeight: 20
  }
});
