import { useLanguage } from "../../application/LanguageContext";
import { useCallback, useEffect, useState } from "react";
import { Text, View } from "react-native";
import { useScopedAction } from "../../application/useScopedAction";
import type { ScopedActionContext } from "../../application/ScopedActionScope";
import { useAuth } from "../../application/AuthContext";
import type { AccountIdentities, AuthOptions, VerificationChannel, VerificationResponse } from "../../core/api/types";
import { AppButton, Card, ErrorBanner, SectionTitle, StatusPill, TextField } from "../../core/components";
import { colors, spacing } from "../../core/theme";
import { useScreenRefresh } from "../../core/ScreenRefreshContext";

export function AccountIdentityCard() {
  const { t } = useLanguage();
  const { api, username, isDemo, isAuthenticated, isBootstrapping, linkGoogle } = useAuth();
  const [options, setOptions] = useState<AuthOptions | null>(null);
  const [identities, setIdentities] = useState<AccountIdentities | null>(null);
  const [channel, setChannel] = useState<VerificationChannel>("email");
  const [destination, setDestination] = useState("");
  const [verification, setVerification] = useState<VerificationResponse | null>(null);
  const [code, setCode] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const actions = useScopedAction(api, username ?? "", () => { setOptions(null); setIdentities(null); setVerification(null); setCode(""); setDestination(""); setError(null); setMessage(null); });
  const pending = actions.busy !== null;
  const load = useCallback(async (signal?: AbortSignal) => {
    const current = actions.capture();
    if (isDemo || !isAuthenticated || isBootstrapping) return;
    const [nextOptions, nextIdentities] = await Promise.all([api.getAuthOptions(signal), api.getAccountIdentities(signal)]);
    if (!current() || signal?.aborted) return;
    setOptions(nextOptions); setIdentities(nextIdentities); setError(null);
  }, [api, isDemo, isAuthenticated, isBootstrapping, actions.capture]);
  useEffect(() => {
    if (!options || !identities || verification) return;
    setChannel(current => options[current === "email" ? "emailEnabled" : "phoneEnabled"] && !identities[current]
      ? current : options.emailEnabled && !identities.email ? "email" : "phone");
  }, [options, identities, verification]);
  useEffect(() => {
    if (isDemo || !isAuthenticated || isBootstrapping) return;
    void actions.run("identity-load", context => load(context.signal), {
      failed: exception => setError(exception instanceof Error ? exception.message : t("Settings could not be loaded."))
    });
    return actions.cancel;
  }, [load, actions.run, actions.cancel, isDemo, isAuthenticated, isBootstrapping]);

  const run = (action: (context: ScopedActionContext) => Promise<void>) => actions.run("identity", action, {
    started: () => { setError(null); setMessage(null); },
    failed: ex => setError(ex instanceof Error ? ex.message : "Unable to link the account.")
  });
  const refresh = useCallback(() => actions.run("identity-refresh", context => load(context.signal), {
    failed: exception => setError(exception instanceof Error ? exception.message : t("Settings could not be loaded."))
  }), [actions.run, load, t]);
  useScreenRefresh(refresh, pending);

  if (isDemo || !isAuthenticated || isBootstrapping) return null;
  const emailAvailable = Boolean(options?.emailEnabled && !identities?.email);
  const phoneAvailable = Boolean(options?.phoneEnabled && !identities?.phone);
  return <>
    <SectionTitle title={t("Account & sign-in methods")} />
    <Card style={{ gap: spacing.lg }}>
      <Text style={{ color: colors.text }}>{t("Signed in as {0}", username)}</Text>
      {identities ? <View style={{ gap: spacing.md }}>
        <View style={{ flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: spacing.md }}>
          <Text style={{ color: colors.text }}>{t("Google:")}</Text>
          <StatusPill label={identities.googleLinked ? t("Linked") : t("Not linked")} tone={identities.googleLinked ? "success" : "neutral"} />
        </View>
        <View style={{ gap: spacing.xs }}>
          <Text style={{ color: colors.muted }}>{t("Verified email:")}</Text>
          <Text selectable style={{ color: identities.email ? colors.primary : colors.muted }}>{identities.email ?? t("Not linked")}</Text>
        </View>
        <View style={{ gap: spacing.xs }}>
          <Text style={{ color: colors.muted }}>{t("Verified phone:")}</Text>
          <Text selectable style={{ color: identities.phone ? colors.primary : colors.muted }}>{identities.phone ?? t("Not linked")}</Text>
        </View>
      </View> : null}
      {(!identities || !identities.googleLinked || !identities.email || !identities.phone) &&
        <Text style={{ color: colors.muted }}>{t("Link Google, email and phone to this account to keep the same installation and devices. Each contact must be verified.")}</Text>}
      <ErrorBanner message={error} />
      {message ? <Text style={{ color: colors.primary }}>{t(message)}</Text> : null}
      {!identities?.googleLinked ? <AppButton label={t("Link Google account")} variant="secondary" disabled={pending || !options?.googleEnabled || !identities}
        onPress={() => void run(async context => {
          const linked = await linkGoogle();
          if (linked) {
            context.publish(() => { setIdentities(current => current && { ...current, googleLinked: true }); setMessage("Google is linked to this account."); });
            await load(context.signal);
          }
        })} /> : null}
      {identities && (emailAvailable || phoneAvailable) ? <>
        <View style={{ flexDirection: "row", flexWrap: "wrap", gap: spacing.md }}>
          <AppButton label={t("Email")} compact variant={channel === "email" ? "primary" : "ghost"} disabled={pending || !emailAvailable || Boolean(verification)} onPress={() => { setChannel("email"); setDestination(""); }} />
          <AppButton label={t("Phone")} compact variant={channel === "phone" ? "primary" : "ghost"} disabled={pending || !phoneAvailable || Boolean(verification)} onPress={() => { setChannel("phone"); setDestination(""); }} />
        </View>
        <TextField label={channel === "email" ? t("Email to link") : t("Phone to link")} value={destination} onChangeText={setDestination}
          editable={!pending && !verification} keyboardType={channel === "email" ? "email-address" : "phone-pad"} placeholder={channel === "email" ? "you@example.com" : "+48123456789"} />
        {verification ? <>
          <TextField label={t("Verification code")} value={code} onChangeText={value => setCode(value.replace(/[^0-9]/g, "").slice(0, 8))} keyboardType="number-pad" />
          <AppButton label={t("Verify and link")} disabled={pending || !code.trim()} loading={pending}
            onPress={() => void run(async context => {
              await api.linkIdentity(verification.verificationId, code.trim());
              context.publish(() => { setVerification(null); setCode(""); setDestination(""); setMessage("Verified contact linked to this account."); });
              await load(context.signal);
            })} />
          <AppButton label={t("Start again")} variant="ghost" disabled={pending} onPress={() => { setVerification(null); setCode(""); }} />
        </> : <AppButton label={t("Send verification code")} variant="secondary" disabled={pending || !destination.trim()} loading={pending}
          onPress={() => void run(async context => {
            const result = await api.startVerification(channel, destination.trim(), "link", context.signal);
            context.publish(() => setVerification(result));
          })} />}
      </> : options && !options.emailEnabled && !options.phoneEnabled ? <Text style={{ color: colors.muted }}>{t("Contact verification is not available on this server yet.")}</Text> : null}
    </Card>
  </>;
}
