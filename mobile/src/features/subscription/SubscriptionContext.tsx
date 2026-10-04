import { translate as t } from "../../core/i18n";
import { createContext, ReactNode, useContext, useEffect, useMemo, useState } from "react";
import { AppState, Platform } from "react-native";
import { useAuth } from "../../application/AuthContext";
import { solarSubscriptions } from "../../../modules/solar-subscriptions/src";
import { hasServerAccess } from "./billingPolicy";
import { SubscriptionController, SubscriptionState } from "./SubscriptionController";

type SubscriptionContextValue = SubscriptionState & {
  hasAccess: boolean; canPurchase: boolean; canUseAppStore: boolean;
  refresh: () => Promise<void>; purchase: (productId: string) => Promise<void>;
  restore: () => Promise<void>; manage: () => Promise<void>;
};
const SubscriptionContext = createContext<SubscriptionContextValue | null>(null);

export function SubscriptionProvider({ children }: { children: ReactNode }) {
  const { api } = useAuth();
  const controller = useMemo(() => new SubscriptionController(api, Platform.OS === "ios" ? solarSubscriptions : null), [api]);
  const [state, setState] = useState(controller.value);
  useEffect(() => {
    const observe = controller.subscribe(setState);
    const denied = api.onBillingDenied(() => controller.billingDenied());
    const listener = solarSubscriptions?.addListener("entitlementsChanged", () => { void controller.refresh(); });
    const foreground = AppState.addEventListener("change", value => controller.appStateChanged(value === "active"));
    const timer = setInterval(() => { if (AppState.currentState === "active") void controller.refresh(); }, 60_000);
    controller.start();
    return () => { controller.stop(); observe(); denied(); listener?.remove(); foreground.remove(); clearInterval(timer); };
  }, [api, controller]);
  return <SubscriptionContext.Provider value={{ ...state, hasAccess: hasServerAccess(state.access),
    canPurchase: controller.canPurchase, canUseAppStore: controller.canUseAppStore,
    refresh: controller.refresh, purchase: controller.purchase, restore: controller.restore, manage: controller.manage
  }}>{children}</SubscriptionContext.Provider>;
}
export function useSubscription() {
  const context = useContext(SubscriptionContext);
  if (!context) throw new Error(t("useSubscription must be used inside SubscriptionProvider."));
  return context;
}
