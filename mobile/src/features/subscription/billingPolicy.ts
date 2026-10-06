import { translate as t } from "../../core/i18n";
export const subscriptionProductIds = {
  monthly: "com.dshapar.solar.monthly",
  yearly: "com.dshapar.solar.yearly"
} as const;

export type BillingAccess = {
  status: "trial" | "active" | "expired";
  hasAccess: boolean;
  trialEndsAt: string;
  subscriptionExpiresAt: string | null;
  accessValidUntil: string | null;
  appAccountToken: string;
  socketLimit: number | null;
  socketUsage?: number;
  appleSubscriptionsEnabled: boolean;
  serverNow: string;
};

export function readBillingAccess(value: unknown): BillingAccess {
  const access = value as Partial<BillingAccess> | null;
  const timestamp = (date: unknown) => typeof date === "string" && Number.isFinite(Date.parse(date));
  if (!access || !["trial", "active", "expired"].includes(access.status ?? "")
    || typeof access.hasAccess !== "boolean" || !timestamp(access.trialEndsAt) || !timestamp(access.serverNow)
    || !(access.subscriptionExpiresAt === null || timestamp(access.subscriptionExpiresAt))
    || !(access.accessValidUntil === null || timestamp(access.accessValidUntil))
    || access.hasAccess && (!access.accessValidUntil || Date.parse(access.accessValidUntil) <= Date.parse(access.serverNow!))
    || !access.hasAccess && access.accessValidUntil !== null
    || typeof access.appAccountToken !== "string" || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(access.appAccountToken)
    || access.appAccountToken === "00000000-0000-0000-0000-000000000000"
    || !(access.socketLimit === null || access.socketLimit === 1)
    || typeof access.appleSubscriptionsEnabled !== "boolean"
    || access.hasAccess && (access.status === "expired"
      || access.status === "trial" && Date.parse(access.trialEndsAt!) <= Date.parse(access.serverNow!)
      || access.status === "active" && (!access.subscriptionExpiresAt
        || Date.parse(access.subscriptionExpiresAt) <= Date.parse(access.serverNow!)))) {
    throw new Error(t("The server returned invalid account access. Please try again."));
  }
  return access as BillingAccess;
}

export function hasServerAccess(access: BillingAccess | null): boolean {
  return Boolean(access?.hasAccess === true && (access.status === "trial" || access.status === "active"));
}

export type BillingProduct = {
  id: string;
  title: string;
  displayPrice: string;
  currencyCode: string;
  type: "autoRenewable" | "unsupported";
  periodUnit: "day" | "week" | "month" | "year" | "unknown";
  periodValue: number;
  subscriptionGroupId: string;
  introEligible: boolean;
  introductoryOffer: {
    paymentMode: "freeTrial" | "payAsYouGo" | "payUpFront" | "unknown";
    periodUnit: BillingProduct["periodUnit"];
    periodValue: number;
    periodCount: number;
  } | null;
};

export type StoreKitEntitlement = {
  productId: string;
  transactionId: string;
  originalTransactionId: string;
  verified: boolean;
  source: "storekit-current-entitlements" | "storekit-unfinished";
  expiresAt: string | null;
  appAccountToken: string | null;
  // Keep the JWS in memory until authenticated server verification acknowledges it.
  signedTransaction: string;
};

export type BillingSnapshot = {
  products: BillingProduct[];
  entitlements: StoreKitEntitlement[];
  pendingTransactions: StoreKitEntitlement[];
  catalogReady: boolean;
  canMakePayments: boolean;
  catalogError: string | null;
  checkedAt: string;
};

export type PurchaseResult = {
  outcome: "purchased" | "pending" | "cancelled";
  snapshot: BillingSnapshot;
};

function isExpectedProduct(product: BillingProduct): boolean {
  const expectedUnit = product.id === subscriptionProductIds.monthly ? "month"
    : product.id === subscriptionProductIds.yearly ? "year" : null;
  return expectedUnit !== null && product.type === "autoRenewable" && product.periodUnit === expectedUnit
    && product.periodValue === 1 && Boolean(product.displayPrice.trim()) && Boolean(product.subscriptionGroupId);
}

export function canPurchaseSubscriptions(snapshot: BillingSnapshot | null): boolean {
  if (!snapshot?.catalogReady || !snapshot.canMakePayments || snapshot.products.length !== 2) return false;
  const monthly = snapshot.products.find(product => product.id === subscriptionProductIds.monthly);
  const yearly = snapshot.products.find(product => product.id === subscriptionProductIds.yearly);
  return Boolean(monthly && yearly && isExpectedProduct(monthly) && isExpectedProduct(yearly)
    && monthly.subscriptionGroupId === yearly.subscriptionGroupId
    && snapshot.products.every(product => !product.introEligible || product.introductoryOffer?.paymentMode !== "freeTrial"));
}

export function transactionsForAccount(snapshot: BillingSnapshot, appAccountToken: string): StoreKitEntitlement[] {
  const transactions = new Map<string, StoreKitEntitlement>();
  for (const transaction of [...snapshot.entitlements, ...snapshot.pendingTransactions]) {
    if (transaction.verified !== true || !transaction.signedTransaction
      || !["storekit-current-entitlements", "storekit-unfinished"].includes(transaction.source)
      || !Object.values(subscriptionProductIds).some(id => id === transaction.productId)
      || transaction.appAccountToken?.toLowerCase() !== appAccountToken.toLowerCase()) continue;
    transactions.set(transaction.transactionId, transaction);
  }
  return [...transactions.values()];
}

export function subscriptionPriceLabel(product: BillingProduct): string {
  return t("{0} per {1}", product.displayPrice, product.periodUnit === "year" ? t("year") : t("month"));
}
