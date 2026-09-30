export const subscriptionProductIds = {
  monthly: "com.dshapar.solar.monthly",
  yearly: "com.dshapar.solar.yearly"
} as const;

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
  source: "storekit-current-entitlements";
  expiresAt: string | null;
  appAccountToken: string | null;
  // Send to an authenticated verification endpoint if server-side account
  // entitlements are integrated; do not log or persist this signed payload.
  signedTransaction: string;
};

export type BillingSnapshot = {
  products: BillingProduct[];
  entitlements: StoreKitEntitlement[];
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
    && monthly.subscriptionGroupId === yearly.subscriptionGroupId);
}

export function hasVerifiedSubscription(snapshot: BillingSnapshot | null): boolean {
  return Boolean(snapshot?.entitlements.some(entitlement => entitlement.verified === true
    && entitlement.source === "storekit-current-entitlements"
    && Object.values(subscriptionProductIds).some(id => id === entitlement.productId)));
}

export function isEligibleFor14DayTrial(product: BillingProduct): boolean {
  const offer = product.introductoryOffer;
  return isExpectedProduct(product) && product.introEligible === true && offer?.paymentMode === "freeTrial"
    && offer.periodCount === 1
    && ((offer.periodUnit === "day" && offer.periodValue === 14) || (offer.periodUnit === "week" && offer.periodValue === 2));
}

export function subscriptionPriceLabel(product: BillingProduct): string {
  return `${product.displayPrice} per ${product.periodUnit === "year" ? "year" : "month"}`;
}
