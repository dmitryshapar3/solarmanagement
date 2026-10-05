import assert from "node:assert/strict";
import { test } from "node:test";
import { SubscriptionController, type SubscriptionStore } from "../src/features/subscription/SubscriptionController";
import { hasServerAccess, type BillingAccess, type BillingSnapshot } from "../src/features/subscription/billingPolicy";

const serverNow = "2026-10-05T12:00:00.000Z";
function grant(duration = 10_000, native = false): BillingAccess {
  return { status: "trial", hasAccess: true, trialEndsAt: "2026-11-05T12:00:00Z", subscriptionExpiresAt: null,
    accessValidUntil: new Date(Date.parse(serverNow) + duration).toISOString(), serverNow,
    appAccountToken: "11111111-1111-4111-8111-111111111111", socketLimit: 1, appleSubscriptionsEnabled: native };
}
const emptySnapshot: BillingSnapshot = { products: [], entitlements: [], pendingTransactions: [], catalogReady: false,
  canMakePayments: false, catalogError: null, checkedAt: serverNow };
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(done => { resolve = done; }); return { promise, resolve }; }
async function settled(controller: SubscriptionController) {
  if (!controller.value.isChecking) return;
  await new Promise<void>(resolve => {
    let unsubscribe = () => {};
    unsubscribe = controller.subscribe(state => { if (!state.isChecking) { unsubscribe(); resolve(); } });
  });
}
async function fixture(native = false) {
  let elapsed = 0, wall = 10_000;
  let read = async () => grant(10_000, native);
  let snapshot = async () => emptySnapshot;
  let verify = async () => grant(10_000, native);
  const store: SubscriptionStore = {
    getSnapshotAsync: () => snapshot(), restoreAsync: () => snapshot(), purchaseAsync: async () => ({ outcome: "cancelled", snapshot: emptySnapshot }),
    manageAsync: async () => {}, finishAsync: async () => {}
  };
  const controller = new SubscriptionController({ getBillingAccess: () => read(), verifyAppleTransaction: () => verify() }, native ? store : null,
    () => elapsed, () => wall);
  controller.start(); await settled(controller);
  return { controller, clocks: (nextElapsed: number, nextWall: number) => { elapsed = nextElapsed; wall = nextWall; },
    read: (next: typeof read) => { read = next; }, snapshot: (next: typeof snapshot) => { snapshot = next; }, verify: (next: typeof verify) => { verify = next; } };
}

test("phone sleep ages cached access even when iOS monotonic time barely advances", async () => {
  const h = await fixture();
  try {
    h.controller.appStateChanged(false);
    const next = deferred<BillingAccess>(); h.read(() => next.promise);
    h.clocks(20, 21_000);
    h.controller.appStateChanged(true);
    assert.equal(hasServerAccess(h.controller.value.access), false, "The delayed deadline timer cannot preserve a grant across sleep");
    next.resolve({ ...grant(), status: "expired", hasAccess: false, accessValidUntil: null });
    await settled(h.controller);
    assert.equal(hasServerAccess(h.controller.value.access), false);
  } finally { h.controller.stop(); }
});

test("failed access checks keep the original verified age and refresh closes expired cache synchronously", async () => {
  const h = await fixture();
  try {
    h.read(async () => { throw new Error("offline"); });
    h.clocks(5_000, 15_000); await h.controller.refresh();
    assert.equal(hasServerAccess(h.controller.value.access), true);
    h.clocks(11_000, 21_000);
    const observed: boolean[] = []; const unsubscribe = h.controller.subscribe(state => observed.push(hasServerAccess(state.access)));
    await h.controller.refresh(); unsubscribe();
    assert.equal(hasServerAccess(h.controller.value.access), false);
    assert.ok(observed.slice(1).every(value => value === false), "No refresh notification may expose already expired cached data");
  } finally { h.controller.stop(); }
});

test("a wall clock regression cannot extend a retained server grant", async () => {
  const h = await fixture();
  try {
    const next = deferred<BillingAccess>(); h.read(() => next.promise);
    h.clocks(50, 9_000); h.controller.appStateChanged(true);
    assert.equal(hasServerAccess(h.controller.value.access), false);
  } finally { h.controller.stop(); }
});

test("shortened server grants expire before slow StoreKit processing and never reopen for one frame", async () => {
  const h = await fixture(true);
  try {
    h.read(async () => grant(100, true));
    const slow = deferred<BillingSnapshot>(); h.snapshot(() => slow.promise);
    const work = h.controller.refresh(); await Promise.resolve();
    assert.equal(h.controller.value.access?.accessValidUntil, grant(100, true).accessValidUntil, "The latest server bound applies before StoreKit returns");
    h.clocks(200, 10_200);
    const observed: boolean[] = []; const unsubscribe = h.controller.subscribe(state => observed.push(hasServerAccess(state.access)));
    slow.resolve(emptySnapshot); await work; unsubscribe();
    assert.equal(hasServerAccess(h.controller.value.access), false);
    assert.ok(observed.slice(1).every(value => value === false));
  } finally { h.controller.stop(); }
});

test("a failed receipt verification cannot replace the age of a successful server access response", async () => {
  const h = await fixture(true);
  try {
    h.clocks(5_000, 15_000);
    h.snapshot(async () => {
      h.clocks(9_000, 19_000);
      return { ...emptySnapshot, entitlements: [{ productId: "com.dshapar.solar.monthly", transactionId: "1", originalTransactionId: "1",
        verified: true, source: "storekit-current-entitlements", expiresAt: "2026-11-05T12:00:00Z", appAccountToken: grant().appAccountToken, signedTransaction: "receipt" }] };
    });
    h.verify(async () => { throw new Error("receipt delivery unavailable"); });
    await h.controller.refresh(); assert.equal(hasServerAccess(h.controller.value.access), true);
    const next = deferred<BillingAccess>(); h.read(() => next.promise);
    h.clocks(16_000, 26_000); h.controller.appStateChanged(true);
    assert.equal(hasServerAccess(h.controller.value.access), false, "Failed receipt attempts do not restart the verified server access window");
  } finally { h.controller.stop(); }
});
