import { ReactNode, useState } from "react";
import { Linking, Pressable, StyleSheet, Text, View } from "react-native";
import { CreditCard, LogOut, RefreshCcw } from "lucide-react-native";
import { AppButton, Card, ErrorBanner, Header, Screen } from "../../core/components";
import { colors, spacing, typography } from "../../core/theme";
import { isEligibleFor14DayTrial, subscriptionPriceLabel, subscriptionProductIds } from "./billingPolicy";
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
  const billing = useSubscription();
  const [selectedId, setSelectedId] = useState<string>(subscriptionProductIds.monthly);
  const [linkError, setLinkError] = useState<string | null>(null);
  const product = billing.snapshot?.products.find(item => item.id === selectedId);
  const hasTrial = Boolean(product && isEligibleFor14DayTrial(product));

  async function openLink(value: string) {
    setLinkError(null);
    try {
      const url = new URL(value);
      if (url.protocol !== "https:" || url.username || url.password) throw new Error("Invalid public URL");
      await Linking.openURL(url.toString());
    } catch {
      setLinkError("The link could not be opened. Please try again or contact support.");
    }
  }

  return <Screen>
    <Header title={billing.hasAccess ? "Your subscription" : "Solar Premium"}
      subtitle="Solar energy monitoring, generation and sales insights"
      action={<AppButton label="Logout" icon={LogOut} onPress={() => void onLogout()} variant="secondary" compact />} />
    <ErrorBanner message={linkError ?? billing.error} />
    {billing.isChecking ? <Text style={styles.copy}>Checking your App Store subscription…</Text> : null}
    {billing.notice ? <Text style={styles.copy}>{billing.notice}</Text> : null}

    {billing.hasAccess ? <Card>
      <Text style={styles.heading}>Subscription active</Text>
      <Text style={styles.copy}>Your access is verified by the App Store. Manage renewal and cancellation using your Apple Account.</Text>
    </Card> : <>
      <Card style={styles.card}>
        <Text style={styles.heading}>Choose your plan</Text>
        {Object.entries(subscriptionProductIds).map(([name, id]) => {
          const option = billing.snapshot?.products.find(item => item.id === id);
          return <Pressable key={id} accessibilityRole="button" accessibilityState={{ selected: selectedId === id, disabled: !billing.canPurchase }}
            disabled={!billing.canPurchase || billing.busy !== null} onPress={() => setSelectedId(id)}
            style={[styles.plan, selectedId === id && styles.selected]}>
            <View style={styles.planCopy}>
              <Text style={styles.planName}>{name === "monthly" ? "Monthly" : "Yearly"}</Text>
              <Text style={styles.copy}>{option && billing.canPurchase ? subscriptionPriceLabel(option) : "Price unavailable"}</Text>
            </View>
            {option && billing.canPurchase && isEligibleFor14DayTrial(option) ? <Text style={styles.trial}>14 days free</Text> : null}
          </Pressable>;
        })}
        {!billing.canPurchase && !billing.isChecking ? <Text style={styles.copy}>
          {billing.snapshot?.catalogError ?? "Subscriptions are currently unavailable for this Apple Account. You can still restore purchases, view policies or contact support."}
        </Text> : null}
        {product && billing.canPurchase ? <Text style={styles.copy}>
          {hasTrial ? `14 days free, then ${subscriptionPriceLabel(product)}.` : `${subscriptionPriceLabel(product)}.`}
          {" "}Your subscription renews automatically until canceled.
        </Text> : null}
        <AppButton label={hasTrial && billing.canPurchase ? "Start 14-day free trial" : "Subscribe"} icon={CreditCard}
          disabled={!billing.canPurchase || !product || billing.busy !== null} loading={billing.busy === "purchase"}
          onPress={() => void billing.purchase(selectedId)} />
      </Card>
      <Text style={styles.copy}>Payment is charged to your Apple Account when the App Store confirms the purchase. If an eligible free trial is shown, payment starts when the trial ends. Cancel in App Store account settings at least 24 hours before the current period ends to avoid renewal. Any introductory offer and final price are confirmed by the App Store before purchase.</Text>
    </>}

    <AppButton label="Restore purchases" icon={RefreshCcw} variant="secondary" disabled={billing.busy !== null}
      loading={billing.busy === "restore"} onPress={() => void billing.restore()} />
    <AppButton label="Manage subscription" variant="ghost" disabled={billing.busy !== null}
      loading={billing.busy === "manage"} onPress={() => void billing.manage()} />
    <AppButton label="Check again" variant="ghost" disabled={billing.busy !== null || billing.isChecking}
      onPress={() => void billing.refresh()} />
    <View style={styles.legal}>
      <AppButton label="Privacy policy" variant="ghost" onPress={() => void openLink(privacyUrl)} />
      <AppButton label="Terms of use" variant="ghost" onPress={() => void openLink(termsUrl)} />
      <AppButton label="Support" variant="ghost" onPress={() => void openLink(supportUrl)} />
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
  trial: { color: colors.primary, fontSize: typography.caption, fontWeight: "700" }, legal: { gap: spacing.sm }
});
