# SolarSubscriptions integration

This local iOS Expo Module calls Apple's StoreKit 2 directly. It is already integrated through Expo autolinking and the iOS Pods, and it compiles in Build 2. No third-party billing account or JavaScript package change is required.

## Current TestFlight build

`mobile/src/application/releaseConfig.ts` sets `appStoreSubscriptionsEnabled = false`. Build 2 retains the StoreKit module and subscription screens for future work, but the authenticated navigator opens real Solar server access without a purchase or product-catalog dependency. The More screen hides the subscription entry; its route also checks the flag before rendering any billing screen. The offline demo remains free and uses a separate local client. This TestFlight build must keep the flag disabled.

## Future App Store activation

1. Complete App Store Connect's paid-app agreement, banking and tax setup, and configure both auto-renewable products in **one** subscription group (suggested display name: Solar Premium): `com.dshapar.solar.monthly` with a one-month period, and `com.dshapar.solar.yearly` with a one-year period. Set the approved base prices of USD 4.99/month and USD 29.99/year and a two-week free introductory offer on each product. Complete product localization, review metadata and availability. The app uses Apple's localized `displayPrice` and customer offer eligibility.
2. Prepare and verify an App Review purchase/restore path that does not require production hardware credentials. Explain before purchase that Solar Premium requires an existing compatible Solar server account and installation; subscribing does not create or connect one, and the demo is always free. Keep login/logout, recovery, privacy, terms and support reachable. Require explicit acknowledgement of the compatible account and installation before purchasing. The current signed-in billing integration does not yet provide that review path.
3. Verify App Store sandbox products and purchase-sheet presentation, trial eligibility/ineligibility, pending/canceled purchases, restore, renewal, natural expiration, grace period, refunds/revocations, account changes and foreground checks. Confirm that public policy/support pages and any commercial weather/data licensing are ready for a paid release. The local StoreKit harness does not prove App Store Connect or sandbox availability.
4. Only after these prerequisites are complete, enable `appStoreSubscriptionsEnabled` in a later App Store build and verify the integrated flow. The existing iOS branch mounts `SubscriptionProvider`, keyed by the Solar account identity, around `SubscriptionGate` and the authenticated navigator; it supplies logout and public HTTPS legal/support links. Changing accounts must remount the provider. Keep demo navigation separate from real authenticated services and StoreKit entitlements.
5. Rebuild and archive the later release, retaining the native module's real StoreKit verification. An unavailable native module, missing products, wrong billing periods/groups, or disallowed payments must disable purchasing. Do not invent prices or trial countdowns; a genuine verified existing entitlement remains usable during a temporary product-catalog outage. Never attach a local StoreKit configuration to the release application's Run scheme or archive.

## Interface

`SubscriptionProvider({ children, appAccountToken? })` accepts an optional stable server-generated UUID for the signed-in Solar account. `useSubscription()` supplies `snapshot`, `isChecking`, `hasAccess`, `canPurchase`, `busy`, `error`, `notice`, and `refresh()`, `purchase(productId)`, `restore()`, `manage()`. The purchase outcome alone never grants access.

`SubscriptionGate({ children, onLogout, privacyUrl, termsUrl, supportUrl })` shows children only for a StoreKit-verified current subscription; otherwise it renders the paywall. `SubscriptionScreen` accepts the same public-link/logout properties and also serves as the active subscriber's management screen. Legal links and logout remain available during loading, pending purchases and unavailable products.

The native module exposes `getSnapshotAsync()`, `getEntitlementsAsync()`, `purchaseAsync(productId, appAccountTokenOrNull)`, `restoreAsync()`, `manageAsync()` and the `entitlementsChanged` event. A foreground-only one-minute refresh checks current entitlements without requesting product catalog data; listeners and timers are removed on provider unmount. Startup and foreground transitions load current product metadata as well. StoreKit updates, revocations, pending approval and verified purchase completion refresh the active set.

## Security and server boundary

Access comes only from `.verified` entries in `Transaction.currentEntitlements` for the two known auto-renewable product IDs. Unverified, revoked and upgraded transactions are ignored. Apple's current-entitlement sequence includes billing grace periods; a device-clock comparison with a transaction's ordinary expiration date would incorrectly revoke such access. Nothing persists an entitlement flag or grants access because a purchase button was pressed.

This module verifies purchases on the Apple device. It does **not** make the existing bearer-authenticated server subscription-aware or bind an Apple subscription to a particular Solar account. Each native entitlement includes its signed transaction JWS and optional `appAccountToken` for authenticated server verification and account binding. Never log or persist those signed payloads in ordinary application storage. A future server integration must verify Apple signatures/bundle/product/environment, bind original transactions to the intended account, process renewals/refunds/revocations and enforce access independently on protected API routes. The JavaScript policy is a client UI gate, not a server authorization boundary.

## Validation and remaining checks

The StoreKit service and Expo bridge pass isolated Swift typechecking with warnings treated as errors against the installed iOS SDK and actual Expo/RN frameworks. TypeScript and the billing policy tests pass, and Expo's autolinking resolver discovers the local module. No real payment was performed. The standalone [local StoreKit regression harness](tests/README.md) passes nine native XCTest cases against this production Swift store with Apple-verified local transactions, including natural expiration under Apple's accelerated clock. Native Pod integration and Build 2 compilation are complete. App Store sandbox verification, the review purchase path and commercial-release prerequisites remain necessary before enabling billing.

Test product unavailability, both plans, genuine two-week eligibility and ineligibility, cancel/pending purchases, successful verification, restore, renewal, expiration, grace period, refund/revocation, foreground refresh and account changes. Keep logout/privacy/support reachable in each state. Do not attach a local StoreKit configuration file to the release archive or substitute fabricated entitlement data in production.

## Primary references

- [Expo local native modules](https://docs.expo.dev/modules/get-started/) and [autolinking](https://docs.expo.dev/modules/autolinking/)
- [Apple verified transactions](https://developer.apple.com/documentation/storekit/transaction) and [current entitlements, including grace periods](https://developer.apple.com/documentation/storekit/transaction/currententitlements)
- [Apple introductory-offer eligibility](https://developer.apple.com/documentation/storekit/product/subscriptioninfo/iseligibleforintrooffer) and [offer periods](https://developer.apple.com/documentation/storekit/product/subscriptionoffer/period)
- [Explicit restore with AppStore.sync](https://developer.apple.com/documentation/storekit/appstore/sync()) and [native subscription management](https://developer.apple.com/documentation/storekit/appstore/showmanagesubscriptions(in:))
