import assert from "node:assert/strict";
import { test } from "node:test";
import { createRequire } from "node:module";
import path from "node:path";
import React from "react";
import { act, create } from "react-test-renderer";
import { build } from "esbuild";
import { ApiClient } from "../src/core/api/ApiClient";
import { DeyeSolarApi } from "../src/core/api/DeyeSolarApi";
import type { BillingAccess, BillingSnapshot } from "../src/features/subscription/billingPolicy";
import { subscriptionProductIds } from "../src/features/subscription/billingPolicy";

const accountToken = "11111111-1111-4111-8111-111111111111";
const foreignToken = "22222222-2222-4222-8222-222222222222";
function access(changes: Partial<BillingAccess> = {}): BillingAccess {
  const value: BillingAccess = { status: "trial", hasAccess: true, trialEndsAt: "2026-11-04T12:00:00Z", subscriptionExpiresAt: null,
    accessValidUntil: "2026-11-04T12:00:00Z", appAccountToken: accountToken, socketLimit: 1, appleSubscriptionsEnabled: false,
    serverNow: "2026-10-04T12:00:00Z", ...changes };
  return { ...value, accessValidUntil: value.hasAccess ? value.subscriptionExpiresAt ?? value.trialEndsAt : null, ...changes };
}
function snapshot(): BillingSnapshot {
  const monthly = { id: subscriptionProductIds.monthly, title: "Monthly", displayPrice: "$4.99", currencyCode: "USD",
    type: "autoRenewable" as const, periodUnit: "month" as const, periodValue: 1, subscriptionGroupId: "group",
    introEligible: false, introductoryOffer: null };
  return { products: [monthly, { ...monthly, id: subscriptionProductIds.yearly, periodUnit: "year", displayPrice: "$29.99" }],
    entitlements: [], pendingTransactions: [], catalogReady: true, canMakePayments: true, catalogError: null,
    checkedAt: "2026-10-04T12:00:00Z" };
}
function transaction(token = accountToken, id = "1") {
  return { productId: subscriptionProductIds.monthly, transactionId: id, originalTransactionId: id,
    verified: true, source: "storekit-current-entitlements" as const, expiresAt: "2026-11-04T12:00:00Z",
    appAccountToken: token, signedTransaction: `signed-${id}` };
}
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(done => { resolve = done; });
  return { promise, resolve };
}

async function harness(platform: "ios" | "android" = "ios", management = false) {
  let serverAccess = access({ appleSubscriptionsEnabled: platform === "ios" });
  let storeSnapshot = snapshot();
  let failure = false;
  let verificationFailure = false;
  let verificationAccess = access({ status: "active", socketLimit: null, appleSubscriptionsEnabled: true,
    subscriptionExpiresAt: "2026-11-04T12:00:00Z" });
  let accessReply: Promise<BillingAccess> | null = null;
  let outcome = "purchased";
  let purchaseReply: Promise<{ outcome: string; snapshot: BillingSnapshot }> | null = null;
  let snapshotReply: Promise<BillingSnapshot> | null = null;
  let finishReply: Promise<void> | null = null;
  let privateMounts = 0;
  let privateUnmounts = 0;
  let selectPrivatePage!: (page: string) => void;
  const calls: string[] = [];
  const finishes: string[] = [];
  const purchases: { id: string; token: string }[] = [];
  const appListeners = new Set<(state: string) => void>();
  let nativeListener: (() => void) | null = null;
  const client = new ApiClient({ baseUrl: "https://solar.example", token: "account-token", transport: async (url, init) => {
    const route = new URL(url).pathname;
    calls.push(route);
    if (route === "/api/devices") return new Response('{"message":"Subscription required"}', { status: 402 });
    if (failure) throw new Error("offline");
    if (route.endsWith("/verify")) {
      calls.push(JSON.parse(init.body!).signedTransaction);
      if (verificationFailure) return new Response('{"message":"Receipt rejected"}', { status: 400 });
      serverAccess = verificationAccess;
      return new Response(JSON.stringify(verificationAccess));
    }
    return new Response(JSON.stringify(accessReply ? await accessReply : serverAccess));
  } });
  const api = new DeyeSolarApi(client);
  const store = {
    getSnapshotAsync: async () => snapshotReply ? await snapshotReply : storeSnapshot,
    getEntitlementsAsync: async () => storeSnapshot,
    purchaseAsync: async (id: string, token: string) => { purchases.push({ id, token }); return purchaseReply ? await purchaseReply : { outcome, snapshot: storeSnapshot }; },
    restoreAsync: async () => storeSnapshot,
    manageAsync: async () => {},
    finishAsync: async (id: string, _token: string) => { calls.push(`finish-${id}`); if (finishReply) await finishReply; finishes.push(id); },
    addListener: (_event: string, listener: () => void) => { nativeListener = listener; return { remove: () => { nativeListener = null; } }; }
  };
  const native = { api, store, platform, appListeners };
  const globals = globalThis as typeof globalThis & { __solarBillingNative?: typeof native; IS_REACT_ACT_ENVIRONMENT?: boolean };
  globals.__solarBillingNative = native;
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  const bundle = await build({
    stdin: { contents: 'export { SubscriptionProvider, useSubscription } from "./src/features/subscription/SubscriptionContext"; export { SubscriptionGate, SubscriptionScreen } from "./src/features/subscription/SubscriptionScreen";', resolveDir: process.cwd(), loader: "ts" },
    bundle: true, write: false, platform: "node", format: "cjs", external: ["react", "react/jsx-runtime"],
    plugins: [{ name: "native-boundaries", setup(builder) {
      builder.onResolve({ filter: /^(react-native|lucide-react-native)$/ }, args => ({ path: args.path, namespace: "native-test" }));
      builder.onResolve({ filter: /(?:^|\/)(AuthContext|LanguageContext|core\/components|ui\/theme\/ThemeProvider)$/ }, args => ({ path: args.path, namespace: "native-test" }));
      builder.onResolve({ filter: /modules\/solar-subscriptions\/src$/ }, () => ({ path: "store", namespace: "native-test" }));
      builder.onLoad({ filter: /.*/, namespace: "native-test" }, args => {
        const state = "globalThis.__solarBillingNative";
        const contents = args.path === "react-native" ? `export const Platform = {OS:${state}.platform}; export const AppState = {currentState:"active", addEventListener: (_event, listener) => { ${state}.appListeners.add(listener); return {remove: () => ${state}.appListeners.delete(listener)}; }}; export const StyleSheet = {create:value=>value}; export const Text="Text",View="View",Pressable="Pressable"; export const Linking={openURL:async()=>{}};`
          : args.path === "lucide-react-native" ? 'export const CreditCard="CreditCard",LogOut="LogOut",RefreshCcw="RefreshCcw",Check="Check",Sun="Sun";'
          : args.path.includes("AuthContext") ? `export const useAuth = () => ({api:${state}.api});`
          : args.path.includes("LanguageContext") ? 'export const useLanguage = () => ({t:(phrase,...args)=>phrase.replace(/\\{(\\d+)\\}/g,(_,slot)=>args[Number(slot)]??"{"+slot+"}")});'
          : args.path.endsWith("ThemeProvider") ? 'export const useTheme=()=>({colors:{ink:"#111",ink2:"#555",ink3:"#888",surface:"#fff",line:"#ddd",fill:"#eee",sunTint:"#ffb",solar:"#fc0"}});'
          : args.path === "store" ? `export const solarSubscriptions = ${state}.platform === "ios" ? ${state}.store : null;`
          : 'import React from "react"; export const AppButton=props=>React.createElement("button",props,props.label); export const Card="Card",ErrorBanner="ErrorBanner",Screen="Screen",DataRow="DataRow",StatusPill="StatusPill",ThemedText="Text"; export const Header=props=>React.createElement("Header",null,props.title,props.subtitle,props.action);';
        return { contents, loader: "js" };
      });
    } }]
  });
  const module = { exports: {} as any };
  new Function("require", "module", "exports", bundle.outputFiles[0]!.text)(createRequire(path.join(process.cwd(), "package.json")), module, module.exports);
  const { SubscriptionProvider, SubscriptionGate, SubscriptionScreen, useSubscription } = module.exports;
  let current: any;
  const renderedAccess: boolean[] = [];
  function Probe() { current = useSubscription(); renderedAccess.push(current.hasAccess); return null; }
  function PrivatePage() {
    const [page, setPage] = React.useState("Home");
    selectPrivatePage = setPage;
    React.useEffect(() => { privateMounts++; return () => { privateUnmounts++; }; }, []);
    return React.createElement("private", { page }, `cached-private-socket-data · ${page}`);
  }
  let renderer: ReturnType<typeof create>;
  const props = { onLogout: async () => {}, privacyUrl: "https://solar.example/privacy", termsUrl: "https://solar.example/terms", supportUrl: "https://solar.example/support" };
  const renderTree = () => React.createElement(SubscriptionProvider, null, React.createElement(Probe),
      management ? React.createElement(SubscriptionScreen, props)
        : React.createElement(SubscriptionGate, props, React.createElement(PrivatePage)));
  await act(async () => {
    renderer = create(renderTree());
  });
  return {
    api, calls, finishes, purchases, renderedAccess, get current() { return current; },
    get tree() { return JSON.stringify(renderer!.toJSON()); },
    get privateMounts() { return privateMounts; }, get privateUnmounts() { return privateUnmounts; },
    selectPage: async (page: string) => { await act(async () => selectPrivatePage(page)); },
    press: async (label: string) => { await act(async () => {
      const button = renderer!.root.findAllByType("button").find(item => item.props.label === label)!;
      assert.ok(button, `The ${label} button must be visible`);
      assert.equal(button.props.disabled, false);
      button.props.onPress();
    }); },
    selectPlan: async (index: number) => { await act(async () => {
      const option = renderer!.root.findAllByType("Pressable")[index]!;
      assert.equal(option.props.disabled, false);
      option.props.onPress();
    }); },
    setAccess: (value: BillingAccess) => { serverAccess = value; }, setSnapshot: (value: BillingSnapshot) => { storeSnapshot = value; },
    setVerificationAccess: (value: BillingAccess) => { verificationAccess = value; },
    fail: (value: boolean) => { failure = value; }, failVerification: (value: boolean) => { verificationFailure = value; },
    delayAccess: (value: Promise<BillingAccess> | null) => { accessReply = value; }, setOutcome: (value: string) => { outcome = value; },
    delayPurchase: (value: Promise<{ outcome: string; snapshot: BillingSnapshot }> | null) => { purchaseReply = value; },
    delaySnapshot: (value: Promise<BillingSnapshot> | null) => { snapshotReply = value; },
    delayFinish: (value: Promise<void> | null) => { finishReply = value; },
    replaceSession: async () => { client.setToken("replacement-account-token"); await act(async () => renderer!.update(renderTree())); },
    foreground: async (state: string) => { await act(async () => { for (const listener of appListeners) listener(state); }); },
    nativeChanged: async () => { await act(async () => { nativeListener?.(); }); },
    close: async () => { await act(async () => renderer!.unmount()); delete globals.__solarBillingNative; delete globals.IS_REACT_ACT_ENVIRONMENT; }
  };
}

test("Android uses the server trial without StoreKit, and a 402 immediately hides cached socket data", async () => {
  const h = await harness("android");
  try {
    assert.equal(h.current.hasAccess, true);
    assert.equal(h.current.canPurchase, false);
    assert.ok(h.tree.includes("cached-private-socket-data"));
    h.setAccess(access({ status: "expired", hasAccess: false }));
    await act(async () => { await assert.rejects(h.api.getDevices()); });
    assert.equal(h.current.hasAccess, false);
    assert.ok(!h.tree.includes("cached-private-socket-data"));
    assert.ok(h.tree.includes("Your trial has ended."));
    assert.ok(h.tree.includes("Logout"));
    assert.ok(h.tree.includes("Privacy policy"));
  } finally { await h.close(); }
});

test("native change callbacks during purchase coalesce and acknowledge the receipt only once", async () => {
  const h = await harness();
  try {
    const purchased = snapshot(); purchased.entitlements = [transaction()]; purchased.pendingTransactions = [transaction()];
    h.setSnapshot(purchased);
    const reply = deferred<{ outcome: string; snapshot: BillingSnapshot }>();
    h.delayPurchase(reply.promise);
    let purchase!: Promise<void>;
    await act(async () => { purchase = h.current.purchase(subscriptionProductIds.monthly); });
    await h.nativeChanged();
    await h.foreground("inactive");
    await h.foreground("active");
    assert.equal(h.current.hasAccess, true, "Native modal lifecycle events retain the still-valid server grant");
    assert.equal(h.privateMounts, 1);
    assert.equal(h.privateUnmounts, 0);
    assert.equal(h.current.busy, "purchase", "The native action keeps modal ownership across inactive/active events");
    assert.deepEqual(h.finishes, []);
    await act(async () => { reply.resolve({ outcome: "purchased", snapshot: purchased }); await purchase; });
    assert.equal(h.current.hasAccess, true);
    assert.equal(h.current.busy, null);
    assert.equal(h.calls.filter(call => call === "signed-1").length, 1);
    assert.deepEqual(h.finishes, ["1"]);
  } finally { await h.close(); }
});

test("a 402 during an unfinished purchase fences its late result and releases busy state", async () => {
  const h = await harness();
  try {
    const purchased = snapshot(); purchased.entitlements = [transaction()]; purchased.pendingTransactions = [transaction()];
    h.setSnapshot(purchased);
    const reply = deferred<{ outcome: string; snapshot: BillingSnapshot }>();
    h.delayPurchase(reply.promise);
    let purchase!: Promise<void>;
    await act(async () => { purchase = h.current.purchase(subscriptionProductIds.monthly); });
    h.setAccess(access({ status: "expired", hasAccess: false, appleSubscriptionsEnabled: true }));
    h.failVerification(true);
    await act(async () => { await assert.rejects(h.api.getDevices()); });
    assert.equal(h.current.hasAccess, false);
    await act(async () => { reply.resolve({ outcome: "purchased", snapshot: purchased }); await purchase; });
    assert.equal(h.current.busy, null);
    assert.equal(h.current.hasAccess, false);
    assert.deepEqual(h.finishes, []);
    assert.ok(!h.tree.includes("cached-private-socket-data"));
    assert.ok(h.tree.includes("Logout"));
    h.failVerification(false);
    await act(async () => { await h.current.refresh(); });
    assert.equal(h.current.hasAccess, true);
    assert.deepEqual(h.finishes, ["1"]);
  } finally { await h.close(); }
});

test("inactive, background and foreground checks preserve the mounted last page within the server grant", async () => {
  const h = await harness("android");
  try {
    await h.selectPage("Sales details · September");
    await h.foreground("inactive");
    await h.foreground("background");
    assert.equal(h.current.hasAccess, true);
    assert.ok(h.tree.includes("Sales details · September"));
    assert.ok(!h.tree.includes("Checking your account access"), "The app switcher retains the current page");
    const reply = deferred<BillingAccess>();
    h.delayAccess(reply.promise);
    await h.foreground("active");
    assert.equal(h.current.hasAccess, true);
    assert.equal(h.current.isChecking, true);
    assert.ok(h.tree.includes("Sales details · September"));
    await h.foreground("active");
    assert.equal(h.privateMounts, 1);
    assert.equal(h.privateUnmounts, 0);
    await act(async () => { reply.resolve(access()); });
    assert.equal(h.current.hasAccess, true);
    h.delayAccess(null);
    h.fail(true);
    await h.foreground("active");
    assert.equal(h.current.hasAccess, true);
    assert.ok(h.tree.includes("Sales details · September"));
    assert.equal(h.privateMounts, 1);
    assert.equal(h.privateUnmounts, 0);
    assert.ok(!h.tree.includes("Your trial has ended."), "Offline is unknown access, not confirmed trial expiration");
  } finally { await h.close(); }
});

test("purchase binds the server account, verifies before finishing, and never submits a neighbor's receipt", async () => {
  const h = await harness();
  try {
    const purchased = snapshot();
    purchased.entitlements = [transaction(), transaction(foreignToken, "foreign")];
    purchased.pendingTransactions = [transaction()];
    h.setSnapshot(purchased);
    await act(async () => { await h.current.purchase(subscriptionProductIds.monthly); });
    assert.deepEqual(h.purchases, [{ id: subscriptionProductIds.monthly, token: accountToken }]);
    assert.equal(h.current.access.status, "active");
    assert.equal(h.current.hasAccess, true);
    assert.deepEqual(h.finishes, ["1"]);
    assert.ok(h.calls.indexOf("signed-1") < h.calls.indexOf("finish-1"));
    assert.ok(!h.calls.includes("signed-foreign"));
  } finally { await h.close(); }
});

test("a rejected signed transaction stays unfinished and restore can recover after server verification succeeds", async () => {
  const h = await harness();
  try {
    const purchased = snapshot(); purchased.entitlements = [transaction()];
    h.setSnapshot(purchased); h.failVerification(true);
    await act(async () => { await h.current.purchase(subscriptionProductIds.monthly); });
    assert.equal(h.current.hasAccess, true, "A rejected receipt cannot erase a still-valid, separately granted server trial");
    assert.equal(h.current.access.status, "trial");
    assert.deepEqual(h.finishes, []);
    assert.ok(h.tree.includes("cached-private-socket-data"));
    h.failVerification(false);
    await act(async () => { await h.current.refresh(); });
    assert.equal(h.current.hasAccess, true);
    h.setAccess(access({ status: "active", appleSubscriptionsEnabled: true, subscriptionExpiresAt: "2026-11-04T12:00:00Z" }));
    await act(async () => { await h.current.restore(); });
    assert.equal(h.current.access.status, "active");
    assert.ok(h.finishes.length > 0);
  } finally { await h.close(); }
});

test("local entitlements and pending approval cannot unlock an expired server account", async () => {
  const h = await harness();
  try {
    const expired = access({ status: "expired", hasAccess: false, appleSubscriptionsEnabled: true });
    h.setAccess(expired); h.setVerificationAccess(expired);
    const purchased = snapshot(); purchased.entitlements = [transaction()];
    h.setSnapshot(purchased);
    await act(async () => { await h.current.refresh(); });
    assert.equal(h.current.hasAccess, false);
    assert.deepEqual(h.finishes, ["1"], "A verified receipt is acknowledged even when its server access is expired");
    h.setSnapshot(snapshot()); h.setOutcome("pending");
    await act(async () => { await h.current.purchase(subscriptionProductIds.monthly); });
    assert.equal(h.current.hasAccess, false);
    assert.ok(h.tree.includes("awaiting App Store approval"));
    assert.ok(!h.tree.includes("cached-private-socket-data"));
  } finally { await h.close(); }
});

test("trial management presents one month and one socket without offering a second local trial", async () => {
  const h = await harness("android", true);
  try {
    assert.ok(h.tree.includes("Free trial"));
    assert.ok(h.tree.includes("You can add one socket during the trial."));
    assert.ok(!h.tree.includes("14-day"));
    assert.ok(!h.tree.includes("Apple Pay"));
  } finally { await h.close(); }
});

test("the server trial deadline hides cached readings while the next authoritative response is pending", async () => {
  const h = await harness("android");
  try {
    const now = "2026-10-04T12:00:00.000Z";
    h.setAccess(access({ serverNow: now, trialEndsAt: "2026-10-04T12:00:00.090Z" }));
    await act(async () => { await h.current.refresh(); });
    assert.equal(h.current.hasAccess, true);
    const next = deferred<BillingAccess>();
    h.delayAccess(next.promise);
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 140)); });
    assert.equal(h.current.hasAccess, false);
    assert.ok(!h.tree.includes("cached-private-socket-data"));
    await act(async () => { next.resolve(access({ status: "expired", hasAccess: false })); });
    assert.equal(h.current.hasAccess, false);
  } finally { await h.close(); }
});

test("the server trust deadline hides paid cached readings before the distant Apple subscription expires", async () => {
  const h = await harness("android");
  try {
    h.setAccess(access({ status: "active", socketLimit: null, serverNow: "2026-10-04T12:00:00.000Z",
      subscriptionExpiresAt: "2026-11-04T12:00:00Z", accessValidUntil: "2026-10-04T12:00:00.090Z" }));
    await act(async () => { await h.current.refresh(); });
    assert.equal(h.current.hasAccess, true);
    const next = deferred<BillingAccess>();
    h.delayAccess(next.promise);
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 140)); });
    assert.equal(h.current.hasAccess, false, "Server trust expiry must hide paid data while its refresh is unavailable");
    assert.ok(!h.tree.includes("cached-private-socket-data"));
    await act(async () => { next.resolve(access({ status: "expired", hasAccess: false })); });
    assert.equal(h.current.hasAccess, false);
  } finally { await h.close(); }
});

test("the real paywall selects the yearly plan and activates server access through its purchase button", async () => {
  const h = await harness();
  try {
    h.setAccess(access({ status: "expired", hasAccess: false, appleSubscriptionsEnabled: true }));
    await act(async () => { await h.current.refresh(); });
    const purchased = snapshot();
    purchased.entitlements = [{ ...transaction(), productId: subscriptionProductIds.yearly }];
    h.setSnapshot(purchased);
    await h.selectPlan(1);
    assert.ok(h.tree.includes("$29.99 per year"));
    await h.press("Subscribe");
    assert.deepEqual(h.purchases, [{ id: subscriptionProductIds.yearly, token: accountToken }]);
    assert.equal(h.current.access.status, "active");
    assert.deepEqual(h.finishes, ["1"]);
    assert.ok(h.tree.includes("cached-private-socket-data"));
  } finally { await h.close(); }
});

test("authoritative denial closes the gate before a slow StoreKit snapshot returns", async () => {
  const h = await harness();
  try {
    const slow = deferred<BillingSnapshot>();
    h.delaySnapshot(slow.promise);
    h.setAccess(access({ status: "expired", hasAccess: false, appleSubscriptionsEnabled: true }));
    let work!: Promise<void>;
    await act(async () => { work = h.current.refresh(); });
    assert.equal(h.current.isChecking, true);
    assert.equal(h.current.hasAccess, false);
    assert.ok(!h.tree.includes("cached-private-socket-data"));
    assert.equal(h.privateUnmounts, 1);
    await act(async () => { slow.resolve(snapshot()); await work; });
    assert.equal(h.current.hasAccess, false);
  } finally { await h.close(); }
});

test("a verified expired receipt closes the gate before native transaction finishing", async () => {
  const h = await harness();
  try {
    const purchased = snapshot(); purchased.entitlements = [transaction()];
    h.setSnapshot(purchased);
    h.setVerificationAccess(access({ status: "expired", hasAccess: false, appleSubscriptionsEnabled: true }));
    const slow = deferred<void>(); h.delayFinish(slow.promise);
    let work!: Promise<void>;
    await act(async () => { work = h.current.refresh(); });
    assert.equal(h.current.hasAccess, false);
    assert.ok(!h.tree.includes("cached-private-socket-data"));
    assert.deepEqual(h.finishes, []);
    await act(async () => { slow.resolve(); await work; });
    assert.equal(h.current.hasAccess, false);
    assert.deepEqual(h.finishes, ["1"]);
  } finally { await h.close(); }
});

test("a replacement session renders closed immediately and rejects the previous account's late grant", async () => {
  const h = await harness("android");
  try {
    await h.selectPage("Old account device details");
    const old = deferred<BillingAccess>(); h.delayAccess(old.promise);
    let work!: Promise<void>;
    await act(async () => { work = h.current.refresh(); });
    const next = deferred<BillingAccess>(); h.delayAccess(next.promise);
    const before = h.renderedAccess.length;
    await h.replaceSession();
    assert.equal(h.current.hasAccess, false);
    assert.ok(h.renderedAccess.slice(before).every(value => value === false), "No render may carry the old controller's grant");
    assert.ok(!h.tree.includes("Old account device details"));
    await act(async () => { old.resolve(access()); await work; });
    assert.equal(h.current.hasAccess, false);
    await act(async () => { next.resolve(access({ status: "expired", hasAccess: false })); });
    assert.equal(h.current.hasAccess, false);
  } finally { await h.close(); }
});
