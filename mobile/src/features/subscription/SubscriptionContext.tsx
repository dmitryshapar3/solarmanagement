import { translate as t } from "../../core/i18n";
import { createContext, ReactNode, useCallback, useContext, useEffect, useRef, useState } from "react";
import { AppState, Platform } from "react-native";
import { solarSubscriptions } from "../../../modules/solar-subscriptions/src";
import { BillingSnapshot, canPurchaseSubscriptions, hasVerifiedSubscription, PurchaseResult } from "./billingPolicy";

type BillingAction = "purchase" | "restore" | "manage";
type SubscriptionContextValue = {
  snapshot: BillingSnapshot | null;
  isChecking: boolean;
  hasAccess: boolean;
  canPurchase: boolean;
  busy: BillingAction | null;
  error: string | null;
  notice: string | null;
  refresh: () => Promise<void>;
  purchase: (productId: string) => Promise<void>;
  restore: () => Promise<void>;
  manage: () => Promise<void>;
};

const SubscriptionContext = createContext<SubscriptionContextValue | null>(null);

export function SubscriptionProvider({ children, appAccountToken }: {
  children: ReactNode;
  // Use a stable server-generated UUID for the authenticated Solar account
  // when server-side signed-transaction verification is integrated.
  appAccountToken?: string;
}) {
  const [snapshot, setSnapshot] = useState<BillingSnapshot | null>(null);
  const [isChecking, setIsChecking] = useState(true);
  const [busy, setBusy] = useState<BillingAction | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const mounted = useRef(false);
  const reading = useRef(false);
  const entitlementReading = useRef(false);
  const action = useRef<BillingAction | null>(null);
  const revision = useRef(0);
  const supported = Platform.OS === "ios" && solarSubscriptions !== null;

  const refresh = useCallback(async (reloadProducts = true) => {
    if (!supported || !solarSubscriptions) {
      if (mounted.current) {
        setSnapshot(null);
        setIsChecking(false);
        setError("Subscriptions are available in the iOS app. Please contact support if access cannot be checked.");
      }
      return;
    }
    const pending = reloadProducts ? reading : entitlementReading;
    if (pending.current || action.current) return;
    pending.current = true;
    const generation = revision.current;
    try {
      const next = reloadProducts ? await solarSubscriptions.getSnapshotAsync() : await solarSubscriptions.getEntitlementsAsync();
      if (mounted.current && generation === revision.current) {
        setSnapshot(next);
        setError(null);
      }
    } catch {
      if (mounted.current && generation === revision.current) {
        setSnapshot(null);
        setError("Your App Store subscription could not be checked. Please try again.");
      }
    } finally {
      pending.current = false;
      if (mounted.current && generation === revision.current) setIsChecking(false);
    }
  }, [supported]);

  useEffect(() => {
    mounted.current = true;
    const listener = solarSubscriptions?.addListener("entitlementsChanged", next => {
      if (!mounted.current) return;
      ++revision.current;
      setSnapshot(next);
      setIsChecking(false);
      setError(null);
    });
    const foreground = AppState.addEventListener("change", state => {
      if (state === "active") void refresh();
    });
    // Expiration may occur without a new transaction while the app stays open.
    // Check the authoritative active set without repeatedly fetching products.
    const timer = setInterval(() => {
      if (AppState.currentState === "active") void refresh(false);
    }, 60_000);
    void refresh();
    return () => {
      mounted.current = false;
      ++revision.current;
      listener?.remove();
      foreground.remove();
      clearInterval(timer);
    };
  }, [refresh]);

  const run = useCallback(async (name: BillingAction, operation: () => Promise<void>) => {
    if (action.current) return;
    action.current = name;
    ++revision.current;
    setBusy(name);
    setError(null);
    setNotice(null);
    try {
      await operation();
    } catch (exception) {
      if (mounted.current) setError(exception instanceof Error ? exception.message : "The App Store action could not be completed.");
    } finally {
      action.current = null;
      if (mounted.current) {
        setBusy(null);
        setIsChecking(false);
      }
    }
  }, []);

  const purchase = useCallback(async (productId: string) => {
    const native = solarSubscriptions;
    if (!native || !canPurchaseSubscriptions(snapshot)) {
      setError("Subscriptions are temporarily unavailable. Please try again later.");
      return;
    }
    await run("purchase", async () => {
      const result: PurchaseResult = await native.purchaseAsync(productId, appAccountToken ?? null);
      if (!mounted.current) return;
      setSnapshot(result.snapshot);
      if (result.outcome === "pending") {
        setNotice("Your purchase is awaiting App Store approval. Access starts when Apple verifies the transaction.");
      } else if (result.outcome === "purchased" && !hasVerifiedSubscription(result.snapshot)) {
        setNotice("The purchase was received. Restore purchases to check your current subscription, or contact support.");
      }
    });
  }, [appAccountToken, run, snapshot]);

  const restore = useCallback(async () => {
    const native = solarSubscriptions;
    if (!native) return;
    await run("restore", async () => {
      const next = await native.restoreAsync();
      if (!mounted.current) return;
      setSnapshot(next);
      setNotice(hasVerifiedSubscription(next) ? "Your subscription has been restored." : "No active subscription was found for this Apple Account.");
    });
  }, [run]);

  const manage = useCallback(async () => {
    const native = solarSubscriptions;
    if (!native) return;
    await run("manage", async () => {
      await native.manageAsync();
      const next = await native.getSnapshotAsync();
      if (mounted.current) setSnapshot(next);
    });
  }, [run]);

  return <SubscriptionContext.Provider value={{
    snapshot, isChecking, hasAccess: supported && hasVerifiedSubscription(snapshot),
    canPurchase: supported && canPurchaseSubscriptions(snapshot), busy, error, notice,
    refresh, purchase, restore, manage
  }}>{children}</SubscriptionContext.Provider>;
}

export function useSubscription() {
  const context = useContext(SubscriptionContext);
  if (!context) throw new Error(t("useSubscription must be used inside SubscriptionProvider."));
  return context;
}
