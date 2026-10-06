import { useEffect, useMemo, useRef, useState } from "react";
import { Platform, View } from "react-native";
import * as Crypto from "expo-crypto";
import * as WebBrowser from "expo-web-browser";
import { useAuth } from "../../application/AuthContext";
import { useLanguage } from "../../application/LanguageContext";
import { useScopedAction } from "../../application/useScopedAction";
import type { AccountSecurityProof } from "../../core/api/AccountSecurityApi";
import type { VerificationChannel, VerificationResponse } from "../../core/api/types";
import { AppButton, ErrorBanner, SegmentedControl, TextField, ThemedText as Text } from "../../core/components";
import { useTheme } from "../../ui/theme/ThemeProvider";
import { accountApi, type AccountMethods } from "./accountApi";
import { appleIdentity } from "./appleSignIn";

export function AccountProofForm({ operation, onProof, disabled = false, revision = 0 }: { operation: string; onProof(proof: AccountSecurityProof | null): void; disabled?: boolean; revision?: number }) {
  const { api, apiBaseUrl, isDemo } = useAuth(); const { t } = useLanguage(); const { colors } = useTheme(); const account = useMemo(() => accountApi(api), [api]);
  const publishProof = useRef(onProof); publishProof.current = onProof;
  const [methods, setMethods] = useState<AccountMethods | null>(null); const [password, setPassword] = useState(""); const [channel, setChannel] = useState<VerificationChannel>("email");
  const [challenge, setChallenge] = useState<VerificationResponse | null>(null); const [code, setCode] = useState(""); const [expires, setExpires] = useState(0);
  const [external, setExternal] = useState<string | null>(null); const [error, setError] = useState<string | null>(null); const [retryAt, setRetryAt] = useState(0); const [now, setNow] = useState(Date.now());
  const actions = useScopedAction(api, `${apiBaseUrl}:${operation}:${revision}`, () => { setMethods(null); setPassword(""); setChallenge(null); setCode(""); setExternal(null); setExpires(0); setError(null); });
  const busy = disabled || Boolean(actions.busy); const destination = channel === "email" ? methods?.email : methods?.phone;
  useEffect(() => { if (isDemo) return; void actions.run("methods", async context => { const value = await account.methods(context.signal); context.publish(() => { setMethods(value); setChannel(value.email ? "email" : "phone"); }); }, { failed: e => setError(e instanceof Error ? e.message : "Unable to load sign-in methods.") }); }, [account, actions.run, isDemo]);
  useEffect(() => { const timer = setInterval(() => setNow(Date.now()), 1000); return () => clearInterval(timer); }, []);
  useEffect(() => { publishProof.current(password ? { currentPassword: password } : external && now < expires ? { externalProofId: external } : challenge && now < Date.parse(challenge.expiresAt) && code.length === 6 ? { verificationId: challenge.verificationId, code } : null); }, [password, external, expires, now, challenge, code]);
  const run = (label: string, action: Parameters<typeof actions.run>[1]) => actions.run(label, action, { started: () => setError(null), failed: e => setError(e instanceof Error ? e.message : "Account confirmation failed.") });
  async function externalProof(provider: "Apple" | "Google") { await run("external", async context => {
    const random = async () => Array.from(await Crypto.getRandomBytesAsync(32), byte => byte.toString(16).padStart(2, "0")).join("");
    const verifier = await random(); const state = await random(); const challenge = (await Crypto.digestStringAsync(Crypto.CryptoDigestAlgorithm.SHA256, verifier, { encoding: Crypto.CryptoEncoding.BASE64 })).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
    const flow = await account.startExternal(provider, operation, provider === "Google" ? challenge : undefined, provider === "Google" ? state : undefined, context.signal);
    let identity: Awaited<ReturnType<typeof appleIdentity>> = null;
    if (provider === "Apple") { if (!flow.rawNonce) throw new Error("Apple confirmation is unavailable."); identity = await appleIdentity(flow.rawNonce); if (!identity) return; }
    else {
      if (!flow.authorizationUrl) throw new Error("Google confirmation is unavailable."); const url = new URL(flow.authorizationUrl); const origin = new URL(apiBaseUrl);
      if (url.protocol !== "https:" || url.origin !== origin.origin || url.username || url.password || !url.pathname.endsWith("/auth/google")) throw new Error("The confirmation URL could not be verified.");
      const result = await WebBrowser.openAuthSessionAsync(url.toString(), "deyesolar://auth/callback", { preferEphemeralSession: true }); if (result.type !== "success") return;
      const callback = new URL(result.url);
      if (callback.protocol !== "deyesolar:" || callback.hostname !== "auth" || callback.pathname !== "/callback" || callback.hash || callback.searchParams.getAll("state").length !== 1 || callback.searchParams.get("state") !== state || callback.searchParams.get("confirmed") !== "google" || callback.searchParams.has("error")) throw new Error("The confirmation response could not be verified.");
    }
    if (!context.isCurrent()) return;
    const complete = await account.completeExternal(flow.flowId, identity ?? undefined, context.signal);
    context.publish(() => { setExternal(complete.externalProofId); setExpires(Date.parse(complete.expiresAt)); setPassword(""); setCode(""); setChallenge(null); });
  }); }
  if (isDemo) return <Text style={{ color: colors.ink3 }}>{t("Account changes are unavailable in sample data.")}</Text>;
  return <View style={{ gap: 12 }}><Text style={{ fontSize: 13, color: colors.ink3 }}>{t("Confirm it's you before continuing.")}</Text><ErrorBanner message={error} />
    {methods?.hasPassword !== false ? <TextField label="Current password" value={password} onChangeText={value => { setPassword(value); setExternal(null); setCode(""); }} secureTextEntry maxLength={128} editable={!busy} autoComplete="current-password" /> : null}
    {methods?.email && methods.phone ? <SegmentedControl options={[{ label: "Email", value: "email" }, { label: "Phone", value: "phone" }]} value={channel} onChange={next => { if (!busy) { setChannel(next); setChallenge(null); setCode(""); } }} /> : null}
    {destination ? <><Text style={{ color: colors.ink2 }}>{destination}</Text><AppButton label={retryAt > now ? t("Resend in {0}s", Math.ceil((retryAt - now) / 1000)) : "Send verification code"} variant="secondary" disabled={busy || retryAt > now} onPress={() => void run("code", async context => { const value = await api.accountSecurity.startProof(channel, destination, context.signal); context.publish(() => { setChallenge(value); setCode(""); setRetryAt(Date.now() + value.retryAfterSeconds * 1000); }); })} />{challenge ? <TextField label="Verification code" value={code} onChangeText={value => { setCode(value.replace(/\D/g, "").slice(0, 6)); setPassword(""); setExternal(null); }} keyboardType="number-pad" autoComplete="one-time-code" textContentType="oneTimeCode" editable={!busy} maxLength={6} /> : null}</> : null}
    {methods?.googleLinked ? <AppButton label="Confirm with Google" variant="secondary" disabled={busy} onPress={() => void externalProof("Google")} /> : null}
    {methods?.appleLinked && Platform.OS === "ios" ? <AppButton label="Confirm with Apple" variant="secondary" disabled={busy} onPress={() => void externalProof("Apple")} /> : null}
    {external && now < expires ? <Text style={{ color: colors.positiveText }}>{t("Account confirmed")}</Text> : null}
  </View>;
}
