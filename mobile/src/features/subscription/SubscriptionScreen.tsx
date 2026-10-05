import { useLanguage } from "../../application/LanguageContext";
import { ReactNode, useState } from "react";
import { Linking, Pressable, StyleSheet, Text, View } from "react-native";
import { CreditCard, LogOut, RefreshCcw } from "lucide-react-native";
import { AppButton, Card, ErrorBanner, Header, Screen } from "../../core/components";
import { colors, spacing, typography } from "../../core/theme";
import { subscriptionPriceLabel, subscriptionProductIds } from "./billingPolicy";
import { useSubscription } from "./SubscriptionContext";

export type SubscriptionScreenProps = {
  onLogout: () => Promise<void>;
  privacyUrl: string;
  termsUrl: string;
  supportUrl: string;
};

export function SubscriptionGate({ children, ...screenProps }: SubscriptionScreenProps & { children: ReactNode }) {
  const { hasAccess } = useSubscription();
  return hasAccess ? children : <SubscriptionScreen {...screenProps} />;
}

export function SubscriptionScreen({ onLogout, privacyUrl, termsUrl, supportUrl }: SubscriptionScreenProps) {
  const { t } = useLanguage();
  const billing = useSubscription();
  const [selectedId, setSelectedId] = useState<string>(subscriptionProductIds.monthly);
  const [linkError, setLinkError] = useState<string | null>(null);
  const product = billing.snapshot?.products.find(item => item.id === selectedId);

  async function openLink(value: string) {
    setLinkError(null);
    try {
      const url = new URL(value);
      if (url.protocol !== "https:" || url.username || url.password) throw new Error(t("Invalid public URL"));
      await Linking.openURL(url.toString());
    } catch {
      setLinkError("The link could not be opened. Please try again or contact support.");
    }
  }

  return <Screen refreshing={billing.isChecking} onRefresh={() => billing.busy === null ? billing.refresh() : undefined}>
    <Header title={billing.hasAccess ? t("Your subscription") : "Solar Premium"}
      subtitle={t("Solar energy monitoring, generation and sales insights")}
      action={<AppButton label={t("Logout")} icon={LogOut} onPress={() => void onLogout()} variant="secondary" compact />} />
    <ErrorBanner message={linkError ?? billing.error} />
    {billing.isChecking ? <Text style={styles.copy}>{t("Checking your account access…")}</Text> : null}
    {billing.notice ? <Text style={styles.copy}>{t(billing.notice)}</Text> : null}

    {billing.access?.status === "trial" ? <Card>
      <Text style={styles.heading}>{t("One-month trial")}</Text>
      <Text style={styles.copy}>{t("Your free trial ends on {0}. You can add one socket during the trial.", new Date(billing.access.trialEndsAt).toLocaleDateString())}</Text>
      <Text style={styles.copy}>{t("After the trial ends, a paid subscription is required to read or control sockets.")}</Text>
    </Card> : billing.access?.status === "active" ? <Card>
      <Text style={styles.heading}>{t("Subscription active")}</Text>
      <Text style={styles.copy}>{t("Your subscription is verified by the server. Manage renewal and cancellation using your Apple Account.")}</Text>
    </Card> : billing.access?.status === "expired" ? <Card>
      <Text style={styles.heading}>{t("Subscription required")}</Text>
      <Text style={styles.copy}>{t("Your trial has ended. Subscribe to read or control your sockets.")}</Text>
    </Card> : null}

    {billing.access?.status !== "active" ? <>
      <Card style={styles.card}>
        <Text style={styles.heading}>{t("Choose your plan")}</Text>
        {Object.entries(subscriptionProductIds).map(([name, id]) => {
          const option = billing.snapshot?.products.find(item => item.id === id);
          return <Pressable key={id} accessibilityRole="button" accessibilityState={{ selected: selectedId === id, disabled: !billing.canPurchase }}
            disabled={!billing.canPurchase || billing.busy !== null} onPress={() => setSelectedId(id)}
            style={[styles.plan, selectedId === id && styles.selected]}>
            <View style={styles.planCopy}>
              <Text style={styles.planName}>{name === "monthly" ? t("Monthly") : t("Yearly")}</Text>
              <Text style={styles.copy}>{option && billing.canPurchase ? subscriptionPriceLabel(option) : t("Price unavailable")}</Text>
            </View>
          </Pressable>;
        })}
        {!billing.canPurchase && !billing.isChecking ? <Text style={styles.copy}>
          {t(!billing.canUseAppStore ? "Subscriptions are not configured yet. Contact support."
            : billing.snapshot?.catalogError ?? "Subscriptions are currently unavailable for this Apple Account. You can still restore purchases, view policies or contact support.")}
        </Text> : null}
        {product && billing.canPurchase ? <Text style={styles.copy}>{t("{0}  Your subscription renews automatically until canceled.", `${subscriptionPriceLabel(product)}.`)}</Text> : null}
        <AppButton label={t("Subscribe")} icon={CreditCard}
          disabled={!billing.canPurchase || !product || billing.busy !== null} loading={billing.busy === "purchase"}
          onPress={() => void billing.purchase(selectedId)} />
      </Card>
      <Text style={styles.copy}>{t("Payment is charged to your Apple Account when the App Store confirms the purchase. Cancel in App Store account settings at least 24 hours before the current period ends to avoid renewal. The final price is confirmed by the App Store before purchase.")}</Text>
    </> : null}

    <AppButton label={t("Restore purchases")} icon={RefreshCcw} variant="secondary" disabled={!billing.canUseAppStore || billing.busy !== null || billing.isChecking}
      loading={billing.busy === "restore"} onPress={() => void billing.restore()} />
    <AppButton label={t("Manage subscription")} variant="ghost" disabled={!billing.canUseAppStore || billing.busy !== null || billing.isChecking}
      loading={billing.busy === "manage"} onPress={() => void billing.manage()} />
    <View style={styles.legal}>
      <AppButton label={t("Privacy policy")} variant="ghost" onPress={() => void openLink(privacyUrl)} />
      <AppButton label={t("Terms of use")} variant="ghost" onPress={() => void openLink(termsUrl)} />
      <AppButton label={t("Support")} variant="ghost" onPress={() => void openLink(supportUrl)} />
    </View>
  </Screen>;
}

const styles = StyleSheet.create({
  card: { gap: spacing.lg }, heading: { color: colors.text, fontSize: typography.section, fontWeight: "800" },
  copy: { color: colors.muted, fontSize: typography.caption, lineHeight: 20 },
  plan: { borderWidth: 1, borderColor: colors.border, borderRadius: 8, padding: spacing.md,
    flexDirection: "row", gap: spacing.md, alignItems: "center" },
  selected: { borderColor: colors.primary, backgroundColor: colors.surfaceRaised },
  planCopy: { flex: 1, gap: spacing.xs }, planName: { color: colors.text, fontSize: typography.body, fontWeight: "700" },
  legal: { gap: spacing.sm }
});
