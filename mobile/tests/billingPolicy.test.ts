import assert from "node:assert/strict";
import { test } from "node:test";
import { BillingAccess, BillingProduct, BillingSnapshot, canPurchaseSubscriptions, hasServerAccess,
  readBillingAccess, subscriptionPriceLabel, subscriptionProductIds, transactionsForAccount } from "../src/features/subscription/billingPolicy";

const monthly: BillingProduct = {
  id: subscriptionProductIds.monthly, title: "Solar Monthly", displayPrice: "$4.99", currencyCode: "USD",
  type: "autoRenewable", periodUnit: "month", periodValue: 1, subscriptionGroupId: "test-group",
  introEligible: false, introductoryOffer: null
};
const yearly: BillingProduct = { ...monthly, id: subscriptionProductIds.yearly, displayPrice: "$29.99", periodUnit: "year" };
const snapshot = (changes: Partial<BillingSnapshot> = {}): BillingSnapshot => ({
  products: [monthly, yearly], entitlements: [], pendingTransactions: [], catalogReady: true, canMakePayments: true,
  catalogError: null, checkedAt: "2026-10-04T12:00:00Z", ...changes
});
const accountToken = "11111111-1111-4111-8111-111111111111";
const access = (changes: Partial<BillingAccess> = {}): BillingAccess => {
  const value: BillingAccess = {
    status: "trial", hasAccess: true, trialEndsAt: "2026-11-04T12:00:00Z", subscriptionExpiresAt: null,
    accessValidUntil: "2026-11-04T12:00:00Z", appAccountToken: accountToken, socketLimit: 1, appleSubscriptionsEnabled: false,
    serverNow: "2026-10-04T12:00:00Z", ...changes
  };
  return { ...value, accessValidUntil: value.hasAccess ? value.subscriptionExpiresAt ?? value.trialEndsAt : null, ...changes };
};
const transaction = {
  productId: subscriptionProductIds.monthly, transactionId: "1", originalTransactionId: "1",
  verified: true, source: "storekit-current-entitlements" as const, expiresAt: "2026-11-04T12:00:00Z",
  appAccountToken: accountToken, signedTransaction: "policy-test-fixture"
};

test("server trial and paid states grant access independently of catalog or device billing support", () => {
  assert.equal(hasServerAccess(null), false);
  assert.equal(hasServerAccess(access()), true);
  assert.equal(hasServerAccess(access({ status: "active", socketLimit: null })), true);
  assert.equal(hasServerAccess(access({ status: "expired", hasAccess: false })), false);
  assert.equal(hasServerAccess(access({ status: "expired", hasAccess: true })), false);
  assert.equal(hasServerAccess(access({ hasAccess: false })), false);
});

test("purchasing requires both expected subscription durations in one group", () => {
  assert.equal(canPurchaseSubscriptions(null), false);
  assert.equal(canPurchaseSubscriptions(snapshot()), true);
  for (const unavailable of [snapshot({ products: [], catalogReady: false }), snapshot({ products: [monthly] }),
    snapshot({ canMakePayments: false }), snapshot({ catalogReady: false })]) {
    assert.equal(canPurchaseSubscriptions(unavailable), false);
  }
  for (const invalid of [{ ...yearly, subscriptionGroupId: "different-group" }, { ...yearly, periodValue: 2 },
    { ...yearly, type: "unsupported" as const }, { ...yearly, id: "foreign-product" },
    { ...yearly, displayPrice: "" }, monthly]) {
    assert.equal(canPurchaseSubscriptions(snapshot({ products: [monthly, invalid] })), false);
  }
});

test("an eligible additional Apple free trial cannot be purchased as paid access after the Solar trial", () => {
  const freeOffer = { ...monthly, introEligible: true, introductoryOffer: {
    paymentMode: "freeTrial" as const, periodUnit: "week" as const, periodValue: 2, periodCount: 1
  } };
  assert.equal(canPurchaseSubscriptions(snapshot({ products: [freeOffer, yearly] })), false);
  assert.equal(canPurchaseSubscriptions(snapshot({ products: [{ ...freeOffer, introEligible: false }, yearly] })), true);
});

test("only this account's verified known records are submitted, including unfinished revocations", () => {
  const unfinished = { ...transaction, transactionId: "2", source: "storekit-unfinished" as const };
  const next = snapshot({ entitlements: [transaction], pendingTransactions: [transaction, unfinished] });
  assert.deepEqual(transactionsForAccount(next, accountToken.toUpperCase()), [transaction, unfinished]);
  for (const invalid of [{ ...transaction, verified: false }, { ...transaction, signedTransaction: "" },
    { ...transaction, productId: "foreign-product" }, { ...transaction, appAccountToken: null },
    { ...transaction, appAccountToken: "22222222-2222-4222-8222-222222222222" }]) {
    assert.deepEqual(transactionsForAccount(snapshot({ entitlements: [invalid] }), accountToken), []);
  }
  assert.equal(hasServerAccess(null), false, "An active local purchase never substitutes for server access");
});

test("prices preserve App Store localized display strings", () => {
  assert.equal(subscriptionPriceLabel(monthly), "$4.99 per month");
  assert.equal(subscriptionPriceLabel(yearly), "$29.99 per year");
  assert.equal(subscriptionPriceLabel({ ...monthly, displayPrice: "24,99 zł", currencyCode: "PLN" }), "24,99 zł per month");
});

test("malformed, unbound and internally expired server responses fail closed before screens can read", () => {
  assert.deepEqual(readBillingAccess(access()), access());
  for (const invalid of [null, {}, access({ appAccountToken: "" }), access({ appAccountToken: "00000000-0000-0000-0000-000000000000" }),
    access({ accessValidUntil: null }), access({ accessValidUntil: "invalid" }), access({ accessValidUntil: "2026-10-04T12:00:00Z" }),
    access({ hasAccess: false, accessValidUntil: "2026-11-04T12:00:00Z" }), { ...access(), accessValidUntil: undefined },
    access({ serverNow: "invalid-date" }), access({ trialEndsAt: "2026-10-04T12:00:00Z" }),
    access({ status: "expired", hasAccess: true }), access({ status: "active", subscriptionExpiresAt: null }),
    access({ status: "active", subscriptionExpiresAt: "2026-10-03T12:00:00Z" })]) {
    assert.throws(() => readBillingAccess(invalid));
  }
});
