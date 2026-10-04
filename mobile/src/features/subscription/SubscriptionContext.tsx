import { translate as t } from "../../core/i18n";
import { createContext, ReactNode, useCallback, useContext, useEffect, useRef, useState } from "react";
import { AppState, Platform } from "react-native";
import { useAuth } from "../../application/AuthContext";
import { solarSubscriptions } from "../../../modules/solar-subscriptions/src";
import { BillingAccess, BillingSnapshot, canPurchaseSubscriptions, hasServerAccess, transactionsForAccount } from "./billingPolicy";

type BillingAction = "purchase" | "restore" | "manage";
type SubscriptionContextValue = {
  access: BillingAccess | null;
  snapshot: BillingSnapshot | null;
  isChecking: boolean;
  hasAccess: boolean;
  canPurchase: boolean;
  canUseAppStore: boolean;
  busy: BillingAction | null;
  error: string | null;
  notice: string | null;
  refresh: () => Promise<void>;
  purchase: (productId: string) => Promise<void>;
  restore: () => Promise<void>;
  manage: () => Promise<void>;
};

const SubscriptionContext = createContext<SubscriptionContextValue | null>(null);

export function SubscriptionProvider({ children }: { children: ReactNode }) {
  const { api } = useAuth();
  const [access, setAccess] = useState<BillingAccess | null>(null);
  const [snapshot, setSnapshot] = useState<BillingSnapshot | null>(null);
  const [isChecking, setIsChecking] = useState(true);
  const [busy, setBusy] = useState<BillingAction | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const mounted = useRef(false);
  const pending = useRef<AbortController | null>(null);
  const refreshRequested = useRef(false);
  const action = useRef<BillingAction | null>(null);
  const verified = useRef(new Set<string>());
  const acknowledged = useRef(new Set<string>());
  const checkedAt = useRef(0);
  const supported = Platform.OS === "ios" && solarSubscriptions !== null;

  const current = useCallback((controller: AbortController) => mounted.current
    && pending.current === controller && !controller.signal.aborted, []);

  const synchronize = useCallback(async (next: BillingSnapshot, initial: BillingAccess, controller: AbortController) => {
    let authoritative = initial;
    const native = solarSubscriptions;
    if (!native || !initial.appleSubscriptionsEnabled) return authoritative;
    for (const transaction of transactionsForAccount(next, initial.appAccountToken)) {
      if (!current(controller)) return authoritative;
      if (acknowledged.current.has(transaction.signedTransaction)) continue;
      if (!verified.current.has(transaction.signedTransaction)) {
        checkedAt.current = performance.now();
        authoritative = await api.verifyAppleTransaction(transaction.signedTransaction, controller.signal);
        if (!current(controller)) return authoritative;
        verified.current.add(transaction.signedTransaction);
      }
      // A successful server receipt can report expired access. Acknowledge receipt
      // independently of access, so retries never substitute for server approval.
      await native.finishAsync(transaction.transactionId, initial.appAccountToken);
      if (current(controller)) acknowledged.current.add(transaction.signedTransaction);
    }
    return authoritative;
  }, [api, current]);

  const refresh = useCallback(async () => {
    if (!mounted.current) return;
    if (pending.current || action.current) { refreshRequested.current = true; return; }
    const controller = new AbortController();
    pending.current = controller;
    setIsChecking(true);
    try {
      checkedAt.current = performance.now();
      const authoritative = await api.getBillingAccess(controller.signal);
      let nextAccess = authoritative;
      if (supported && solarSubscriptions && authoritative.appleSubscriptionsEnabled) {
        const next = await solarSubscriptions.getSnapshotAsync();
        if (!current(controller)) return;
        setSnapshot(next);
        nextAccess = await synchronize(next, authoritative, controller);
      }
      if (current(controller)) {
        setAccess(nextAccess);
        setError(null);
      }
    } catch {
      if (current(controller)) {
        setAccess(null);
        setError("Your account access could not be checked. Connect to the server and try again.");
      }
    } finally {
      if (pending.current === controller) pending.current = null;
      if (mounted.current && !controller.signal.aborted) setIsChecking(false);
      if (mounted.current && refreshRequested.current && AppState.currentState === "active") {
        refreshRequested.current = false;
        void refresh();
      }
    }
  }, [api, current, supported, synchronize]);

  useEffect(() => {
    mounted.current = true;
    const invalidate = () => {
      pending.current?.abort();
      pending.current = null;
      refreshRequested.current = false;
      setAccess(null);
      setIsChecking(true);
    };
    const denied = api.onBillingDenied(() => { invalidate(); void refresh(); });
    // Native changes are invalidations; access always comes from the server.
    const listener = solarSubscriptions?.addListener("entitlementsChanged", () => { void refresh(); });
    const foreground = AppState.addEventListener("change", state => {
      // Apple's purchase sheet can make the app inactive. Preserve ownership of
      // that action until it returns, while keeping cached connected data hidden.
      if (action.current) { setAccess(null); refreshRequested.current = true; }
      else invalidate();
      if (state === "active") void refresh();
    });
    const timer = setInterval(() => {
      if (AppState.currentState === "active") void refresh();
    }, 60_000);
    void refresh();
    return () => {
      mounted.current = false;
      pending.current?.abort();
      pending.current = null;
      denied();
      listener?.remove();
      foreground.remove();
      clearInterval(timer);
    };
  }, [api, refresh]);

  // Use the server's remaining duration; the device clock never grants access.
  useEffect(() => {
    const deadline = access?.accessValidUntil;
    if (!hasServerAccess(access) || !deadline) return;
    const milliseconds = Date.parse(deadline) - Date.parse(access!.serverNow) - (performance.now() - checkedAt.current);
    if (!Number.isFinite(milliseconds)) { setAccess(null); return; }
    const timer = setTimeout(() => { setAccess(null); void refresh(); }, Math.max(0, Math.min(milliseconds, 2_147_483_647)));
    return () => clearTimeout(timer);
  }, [access, refresh]);

  const run = useCallback(async (name: BillingAction, operation: (controller: AbortController) => Promise<void>) => {
    if (pending.current || action.current || !mounted.current) return;
    const controller = new AbortController();
    pending.current = controller;
    action.current = name;
    setBusy(name);
    setError(null);
    setNotice(null);
    try {
      await operation(controller);
    } catch (exception) {
      if (current(controller)) {
        setAccess(null);
        setError(exception instanceof Error ? exception.message : "The App Store action could not be completed.");
      }
    } finally {
      action.current = null;
      if (pending.current === controller) pending.current = null;
      if (mounted.current) { setBusy(null); setIsChecking(false); }
      if (mounted.current && refreshRequested.current && AppState.currentState === "active") {
        refreshRequested.current = false;
        void refresh();
      }
    }
  }, [current, refresh]);

  const canUseAppStore = supported && access?.appleSubscriptionsEnabled === true;
  const canPurchase = canUseAppStore && !isChecking && canPurchaseSubscriptions(snapshot);
  const purchase = useCallback(async (productId: string) => {
    const native = solarSubscriptions;
    if (!native || !canPurchase || !access) {
      setError("Subscriptions are temporarily unavailable. Please try again later.");
      return;
    }
    await run("purchase", async controller => {
      const result = await native.purchaseAsync(productId, access.appAccountToken);
      if (!current(controller)) return;
      setSnapshot(result.snapshot);
      checkedAt.current = performance.now();
      const authoritative = await api.getBillingAccess(controller.signal);
      const nextAccess = await synchronize(result.snapshot, authoritative, controller);
      if (!current(controller)) return;
      setAccess(nextAccess);
      if (result.outcome === "pending") {
        setNotice("Your purchase is awaiting App Store approval. Access starts after server verification.");
      } else if (result.outcome === "purchased" && nextAccess.status !== "active") {
        setNotice("The purchase has not activated this account. Restore purchases or contact support.");
      }
    });
  }, [access, api, canPurchase, current, run, synchronize]);

  const restore = useCallback(async () => {
    const native = solarSubscriptions;
    if (!native || !canUseAppStore || !access) return;
    await run("restore", async controller => {
      const next = await native.restoreAsync();
      if (!current(controller)) return;
      setSnapshot(next);
      checkedAt.current = performance.now();
      const authoritative = await api.getBillingAccess(controller.signal);
      const nextAccess = await synchronize(next, authoritative, controller);
      if (!current(controller)) return;
      setAccess(nextAccess);
      setNotice(nextAccess.status === "active" ? "Your subscription has been restored."
        : "No active subscription was found for this Solar account. Use the account that made the purchase.");
    });
  }, [access, api, canUseAppStore, current, run, synchronize]);

  const manage = useCallback(async () => {
    const native = solarSubscriptions;
    if (!native || !canUseAppStore) return;
    await run("manage", async () => { await native.manageAsync(); refreshRequested.current = true; });
  }, [canUseAppStore, run]);

  return <SubscriptionContext.Provider value={{
    access, snapshot, isChecking, hasAccess: hasServerAccess(access), canPurchase, canUseAppStore,
    busy, error, notice, refresh, purchase, restore, manage
  }}>{children}</SubscriptionContext.Provider>;
}

export function useSubscription() {
  const context = useContext(SubscriptionContext);
  if (!context) throw new Error(t("useSubscription must be used inside SubscriptionProvider."));
  return context;
}
