import { useState } from "react";
import { Alert, Share, Text, View } from "react-native";
import { useScopedAction } from "../../application/useScopedAction";
import type { ScopedActionContext } from "../../application/ScopedActionScope";
import { useAuth } from "../../application/AuthContext";
import { useLanguage } from "../../application/LanguageContext";
import type { AccountSecurityProof } from "../../core/api/AccountSecurityApi";
import type { VerificationChannel, VerificationResponse } from "../../core/api/types";
import { AppButton, Card, ErrorBanner, SectionTitle, TextField } from "../../core/components";
import { colors, spacing } from "../../core/theme";

export function AccountSecurityCard() {
  const { api, isDemo, logout, username, apiBaseUrl } = useAuth();
  const { t } = useLanguage();
  const [password, setPassword] = useState("");
  const [newPassword, setNewPassword] = useState("");
  const [channel, setChannel] = useState<VerificationChannel>("email");
  const [destination, setDestination] = useState("");
  const [challenge, setChallenge] = useState<VerificationResponse | null>(null);
  const [code, setCode] = useState("");
  const [error, setError] = useState<string | null>(null);
  const actions = useScopedAction(api, `${username}:${apiBaseUrl}:${isDemo}`, () => {
    setPassword(""); setNewPassword(""); setCode(""); setChallenge(null); setDestination(""); setError(null);
  });
  const busy = actions.busy !== null;
  if (isDemo) return null;
  const proof = (): AccountSecurityProof => password ? { currentPassword: password }
    : { verificationId: challenge?.verificationId, code };
  const hasProof = Boolean(password || challenge && /^\d{6}$/.test(code));
  const run = (action: (context: ScopedActionContext) => Promise<void>, consumeProof = true) => actions.run("security", action, {
    started: () => setError(null),
    failed: ex => setError(ex instanceof Error ? ex.message : t("Action failed.")),
    finished: () => { if (consumeProof) { setPassword(""); setNewPassword(""); setCode(""); setChallenge(null); } }
  });
  async function changeSecurity(context: ScopedActionContext, action: () => Promise<unknown>) {
    await action();
    if (context.isCurrent()) await logout();
  }
  return <Card style={{ gap: spacing.lg }}>
    <SectionTitle title={t("Account security")} />
    <Text style={{ color: colors.muted }}>{t("Confirm your current password or a new verification code to change your password, revoke sessions, export data or delete your account.")}</Text>
    <ErrorBanner message={error} />
    <TextField label={t("Current password")} value={password} onChangeText={v => setPassword(v.slice(0, 128))} secureTextEntry editable={!busy} />
    <View style={{ flexDirection: "row", gap: spacing.md }}>
      <AppButton label={t("Email")} compact variant={channel === "email" ? "primary" : "ghost"} disabled={busy} onPress={() => setChannel("email")} />
      <AppButton label={t("Phone")} compact variant={channel === "phone" ? "primary" : "ghost"} disabled={busy} onPress={() => setChannel("phone")} />
    </View>
    <TextField label={t("Verified email or phone")} value={destination} onChangeText={setDestination} keyboardType={channel === "email" ? "email-address" : "phone-pad"} editable={!busy} />
    <AppButton label={t("Send verification code")} variant="secondary" disabled={busy || !destination.trim()}
      onPress={() => void run(async context => {
        const issued = await api.accountSecurity.startProof(channel, destination.trim());
        context.publish(() => setChallenge(issued));
      }, false)} />
    {challenge ? <TextField label={t("Verification code")} value={code} onChangeText={v => setCode(v.replace(/[^0-9]/g, "").slice(0, 6))} keyboardType="number-pad" editable={!busy} /> : null}
    <TextField label={t("New password")} value={newPassword} onChangeText={v => setNewPassword(v.slice(0, 128))} secureTextEntry editable={!busy} />
    <AppButton label={t("Change password")} disabled={busy || !hasProof || newPassword.length < 12}
      onPress={() => void run(context => changeSecurity(context, () => api.accountSecurity.changePassword(proof(), newPassword)))} />
    <AppButton label={t("Sign out all devices")} variant="secondary" disabled={busy || !hasProof}
      onPress={() => void run(context => changeSecurity(context, () => api.accountSecurity.revokeAll(proof())))} />
    <AppButton label={t("Export account data")} variant="secondary" disabled={busy || !hasProof}
      onPress={() => void run(async context => {
        const data = await api.accountSecurity.exportData(proof());
        if (context.isCurrent()) await Share.share({ message: JSON.stringify(data, null, 2) });
      })} />
    <AppButton label={t("Delete account and owned installations")} variant="secondary" disabled={busy || !hasProof}
      onPress={() => {
        const current = actions.capture();
        Alert.alert(t("Delete account and owned installations"), t("Confirm your current password or a new verification code before continuing."), [
          { text: t("Cancel"), style: "cancel" },
          { text: t("Delete"), style: "destructive", onPress: () => {
            if (current())
              void run(context => changeSecurity(context, () => api.accountSecurity.deleteAccount(proof())));
          } }
        ]);
      }} />
  </Card>;
}
