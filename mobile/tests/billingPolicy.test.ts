import assert from "node:assert/strict";
import { test } from "node:test";
import { BillingProduct, BillingSnapshot, canPurchaseSubscriptions, hasVerifiedSubscription,
  isEligibleFor14DayTrial, subscriptionPriceLabel, subscriptionProductIds } from "../src/features/subscription/billingPolicy";

const monthly: BillingProduct = {
  id: subscriptionProductIds.monthly, title: "Solar Monthly", displayPrice: "$4.99", currencyCode: "USD",
  type: "autoRenewable", periodUnit: "month", periodValue: 1, subscriptionGroupId: "test-group",
  introEligible: true, introductoryOffer: { paymentMode: "freeTrial", periodUnit: "week", periodValue: 2, periodCount: 1 }
};
const yearly: BillingProduct = { ...monthly, id: subscriptionProductIds.yearly, displayPrice: "$29.99", periodUnit: "year" };
const snapshot = (changes: Partial<BillingSnapshot> = {}): BillingSnapshot => ({
  products: [monthly, yearly], entitlements: [], catalogReady: true, canMakePayments: true,
  catalogError: null, checkedAt: "2026-09-30T12:00:00Z", ...changes
});

test("unavailable products, missing native state and pending or canceled purchases never grant access", () => {
  assert.equal(canPurchaseSubscriptions(null), false);
  assert.equal(hasVerifiedSubscription(null), false);
  for (const unavailable of [snapshot({ products: [], catalogReady: false }), snapshot({ products: [monthly] }),
    snapshot({ canMakePayments: false }), snapshot({ catalogReady: false })]) {
    assert.equal(canPurchaseSubscriptions(unavailable), false);
    assert.equal(hasVerifiedSubscription(unavailable), false);
  }
  for (const outcome of ["pending", "cancelled", "purchased"]) {
    const result = { outcome, snapshot: snapshot() };
    assert.equal(hasVerifiedSubscription(result.snapshot), false);
  }
});

test("purchasing requires both expected subscription durations in one group", () => {
  assert.equal(canPurchaseSubscriptions(snapshot()), true);
  for (const invalid of [{ ...yearly, subscriptionGroupId: "different-group" }, { ...yearly, periodValue: 2 },
    { ...yearly, type: "unsupported" as const }, { ...yearly, id: "foreign-product" },
    { ...yearly, displayPrice: "" }, monthly]) {
    assert.equal(canPurchaseSubscriptions(snapshot({ products: [monthly, invalid] })), false);
  }
});

test("a 14-day trial requires the real free-trial offer and Apple's current customer eligibility", () => {
  assert.equal(isEligibleFor14DayTrial(monthly), true);
  assert.equal(isEligibleFor14DayTrial({ ...monthly, introductoryOffer: {
    paymentMode: "freeTrial", periodUnit: "day", periodValue: 14, periodCount: 1
  } }), true);
  assert.equal(isEligibleFor14DayTrial({ ...monthly, introEligible: false }), false);
  assert.equal(isEligibleFor14DayTrial({ ...monthly, introductoryOffer: null }), false);
  for (const offer of [{ ...monthly.introductoryOffer!, paymentMode: "payAsYouGo" as const },
    { ...monthly.introductoryOffer!, periodValue: 1 }, { ...monthly.introductoryOffer!, periodCount: 2 },
    { ...monthly.introductoryOffer!, periodUnit: "month" as const }]) {
    assert.equal(isEligibleFor14DayTrial({ ...monthly, introductoryOffer: offer }), false);
  }
});

test("only current verified entitlements for the app's known products unlock access", () => {
  // This is a policy-only fixture. Runtime entitlements come exclusively from
  // StoreKit's verified currentEntitlements sequence, never from these tests.
  const entitlement = {
    productId: subscriptionProductIds.monthly, transactionId: "1", originalTransactionId: "1",
    verified: true, source: "storekit-current-entitlements" as const, expiresAt: "2026-09-29T12:00:00Z",
    appAccountToken: null, signedTransaction: "policy-test-fixture"
  };
  assert.equal(hasVerifiedSubscription(snapshot({ entitlements: [entitlement] })), true,
    "StoreKit currentEntitlements includes grace periods; device-clock comparisons must not revoke them");
  assert.equal(hasVerifiedSubscription(snapshot({ entitlements: [{ ...entitlement, verified: false }] })), false);
  assert.equal(hasVerifiedSubscription(snapshot({ entitlements: [{ ...entitlement, productId: "foreign-product" }] })), false);
  assert.equal(hasVerifiedSubscription(snapshot({ products: [], catalogReady: false, entitlements: [entitlement] })), true,
    "A verified existing subscription is independent of whether the product catalog can be fetched");
});

test("prices remain the App Store's localized display strings without currency conversion", () => {
  assert.equal(subscriptionPriceLabel(monthly), "$4.99 per month");
  assert.equal(subscriptionPriceLabel(yearly), "$29.99 per year");
  assert.equal(subscriptionPriceLabel({ ...monthly, displayPrice: "24,99 zł", currencyCode: "PLN" }), "24,99 zł per month");
});
