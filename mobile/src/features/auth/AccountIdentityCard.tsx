import { useLanguage } from "../../application/LanguageContext";
import { useEffect, useState } from "react";
import { Text, View } from "react-native";
import { useScopedAction } from "../../application/useScopedAction";
import type { ScopedActionContext } from "../../application/ScopedActionScope";
import { useAuth } from "../../application/AuthContext";
import type { AuthOptions, VerificationChannel, VerificationResponse } from "../../core/api/types";
import { AppButton, Card, ErrorBanner, SectionTitle, TextField } from "../../core/components";
import { colors, spacing } from "../../core/theme";

export function AccountIdentityCard() {
  const { t } = useLanguage();
  const { api, username, isDemo, linkGoogle } = useAuth();
  const [options, setOptions] = useState<AuthOptions | null>(null);
  const [channel, setChannel] = useState<VerificationChannel>("email");
  const [destination, setDestination] = useState("");
  const [verification, setVerification] = useState<VerificationResponse | null>(null);
  const [code, setCode] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const actions = useScopedAction(api, username ?? "", () => { setOptions(null); setVerification(null); setCode(""); setDestination(""); setError(null); setMessage(null); });
  const pending = actions.busy !== null;
  useEffect(() => {
    const current = actions.capture();
    const controller = new AbortController();
    if (!isDemo) void api.getAuthOptions(controller.signal).then(value => {
      if (current() && !controller.signal.aborted) { setOptions(value); setChannel(value.emailEnabled ? "email" : "phone"); }
    }).catch(() => {});
    return () => controller.abort();
  }, [api, isDemo, actions.capture]);

  const run = (action: (context: ScopedActionContext) => Promise<void>) => actions.run("identity", action, {
    started: () => { setError(null); setMessage(null); },
    failed: ex => setError(ex instanceof Error ? ex.message : "Unable to link the account.")
  });

  if (isDemo) return null;
  return <>
    <SectionTitle title={t("Account & sign-in methods")} />
    <Card style={{ gap: spacing.lg }}>
      <Text style={{ color: colors.text }}>{t("Signed in as {0}", username)}</Text>
      <Text style={{ color: colors.muted }}>{t("Link Google, email and phone to this account to keep the same installation and devices. Each contact must be verified.")}</Text>
      <ErrorBanner message={error} />
      {message ? <Text style={{ color: colors.primary }}>{t(message)}</Text> : null}
      <AppButton label={t("Link Google account")} variant="secondary" disabled={pending || !options?.googleEnabled}
        onPress={() => void run(async context => {
          const linked = await linkGoogle();
          if (linked) context.publish(() => setMessage("Google is linked to this account."));
        })} />
      {options?.emailEnabled || options?.phoneEnabled ? <>
        <View style={{ flexDirection: "row", flexWrap: "wrap", gap: spacing.md }}>
          <AppButton label={t("Email")} compact variant={channel === "email" ? "primary" : "ghost"} disabled={pending || !options.emailEnabled || Boolean(verification)} onPress={() => { setChannel("email"); setDestination(""); }} />
          <AppButton label={t("Phone")} compact variant={channel === "phone" ? "primary" : "ghost"} disabled={pending || !options.phoneEnabled || Boolean(verification)} onPress={() => { setChannel("phone"); setDestination(""); }} />
        </View>
        <TextField label={channel === "email" ? t("Email to link") : t("Phone to link")} value={destination} onChangeText={setDestination}
          editable={!pending && !verification} keyboardType={channel === "email" ? "email-address" : "phone-pad"} placeholder={channel === "email" ? "you@example.com" : "+48123456789"} />
        {verification ? <>
          <TextField label={t("Verification code")} value={code} onChangeText={value => setCode(value.replace(/[^0-9]/g, "").slice(0, 8))} keyboardType="number-pad" />
          <AppButton label={t("Verify and link")} disabled={pending || !code.trim()} loading={pending}
            onPress={() => void run(async context => {
              await api.linkIdentity(verification.verificationId, code.trim());
              context.publish(() => { setVerification(null); setCode(""); setDestination(""); setMessage("Verified contact linked to this account."); });
            })} />
          <AppButton label={t("Start again")} variant="ghost" disabled={pending} onPress={() => { setVerification(null); setCode(""); }} />
        </> : <AppButton label={t("Send verification code")} variant="secondary" disabled={pending || !destination.trim()} loading={pending}
          onPress={() => void run(async context => {
            const result = await api.startVerification(channel, destination.trim(), "link", context.signal);
            context.publish(() => setVerification(result));
          })} />}
      </> : <Text style={{ color: colors.muted }}>{t("Contact verification is not available on this server yet.")}</Text>}
    </Card>
  </>;
}
