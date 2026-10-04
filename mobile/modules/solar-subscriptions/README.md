# SolarSubscriptions integration

The iOS Expo module uses StoreKit 2 for App Store auto-renewable subscriptions. Connected access is enforced by the Solar server for every platform. A native entitlement, purchase outcome or cached flag never grants access by itself. The offline demo remains free and does not call StoreKit or the server.

## Trial and account access

The server starts a one-calendar-month trial when an account is registered. Existing accounts receive one month from the billing migration. The trial permits adding one socket; existing socket configuration is retained during upgrade. After the trial, an active server-verified subscription is required to read or control sockets. The server enforces the limit and paid access independently of this UI.

`GET /api/billing/access` returns `status`, `hasAccess`, `trialEndsAt`, `subscriptionExpiresAt`, `accessValidUntil`, `appAccountToken`, `socketLimit`, `appleSubscriptionsEnabled` and `serverNow`. The app validates this response and gates every real authenticated navigator. Unknown or unavailable access starts closed. App foreground transitions and HTTP 402 responses hide cached connected screens and refresh the server state. An active foreground refresh runs once a minute, and an independent deadline timer uses `accessValidUntil` to hide data while renewal state is checked. This server deadline includes the one-hour trust limit for cached Apple status and any remaining account trial; the displayed Apple subscription expiration is a separate value.

The purchase setting comes from the server's validated Apple configuration. The trial works while Apple billing is disabled. Android and web use the same server access check but do not present native purchase actions. Privacy, terms, support, logout and retry remain reachable without paid access.

## Configure Apple subscriptions

1. Complete App Store Connect's Paid Apps Agreement, banking and tax setup. Configure `com.dshapar.solar.monthly` with a one-month period and `com.dshapar.solar.yearly` with a one-year period in the same subscription group. Verify approved prices, localization, review information and availability; the app displays StoreKit's localized price.
2. Remove the previously prepared two-week Apple introductory offers. The free month is the Solar account trial, starts without a purchase, and is not an additional App Store introductory offer. The local StoreKit fixture has no introductory offers. Purchasing is disabled for eligible additional Apple free-trial offers; the server also refuses to extend the Solar trial or grant paid access for a free offer.
3. Configure the backend Apple credentials, trust roots, bundle/application identifiers, environment and Server Notifications V2 endpoint using [backend setup](../../../docs/app-store-backend.md). Keep keys, certificates, signed receipts and account credentials outside source control. Apple Pay is a separate technology; these digital access subscriptions use StoreKit In-App Purchase.
4. Verify Apple's sandbox products and payment sheet, pending/cancelled purchases, restore, renewal, expiration, grace period, refunds, account changes and notifications. Use an isolated review account and installation. The offline demo cannot prove a purchase/restore review path.
5. Compile and test prepared native build `1.0.0 (9)` on macOS before uploading it. Previously uploaded build 8 does not contain this integration. The native and Expo build numbers are aligned at 9; this source update does not upload the app. Do not attach the local `.storekit` configuration to the release app or archive.

## Purchase and delivery contract

`SubscriptionProvider` obtains the signed-in account's stable server-generated UUID. `purchaseAsync(productId, appAccountToken)` requires that token and passes it to StoreKit. Current entitlements and verified unfinished transactions include the Apple-signed JWS, product/transaction identifiers and account token. Only known products bound to the current Solar account are submitted to `POST /api/billing/apple/verify`.

The server verifies the signature, identity, product, environment and account binding, then checks authoritative Apple subscription status before persisting access. A successful HTTP verification response acknowledges receipt even when access is expired. Only then does the app call `finishAsync(transactionId, appAccountToken)`. Native delivery remains unfinished on rejection, offline failure or uncertain completion, so startup/restore can retry. The native finish method refuses a transaction belonging to another Solar account and accepts repeated acknowledgements safely.

StoreKit events invalidate server access state. Reentrant events coalesce while a purchase/restore is pending; they do not grant access. The app preserves native action ownership across Apple's purchase sheet lifecycle and fences late completion after access denial or account replacement. An Apple Account's receipt cannot transfer access to an unrelated Solar account. Restore uses explicit `AppStore.sync()` and the same authenticated verification route. Manage opens Apple's subscription settings.

Signed payloads exist only in memory in the client. They are not logged or written to ordinary device storage. Server-retained purchase/account identifiers require accurate App Privacy disclosure as Purchase History linked to the user for App Functionality.

## Validation

Permanent TypeScript policy and real React provider/screen tests cover the server trial, malformed access, no StoreKit on Android, expired access, 402 cache hiding, foreground/offline behavior, localized prices, binding, neighboring accounts, rejection/retry, pending approval, reentrant native callbacks, late purchase completion and deadline invalidation. Native XCTest source covers 11 local StoreKit scenarios, including mandatory binding and unfinished delivery across restart until server acknowledgement.

The [native harness](tests/README.md) requires macOS with Xcode and an installed iOS 18+ simulator runtime. Windows TypeScript tests and Expo export do not compile or run native Swift. Local StoreKit tests use Apple's simulated transactions and do not prove live Apple sandbox contracts, notifications or payment readiness. Run native and sandbox checks before claiming this release is ready or publishing it.

## Primary references

- [Apple In-App Purchase](https://developer.apple.com/in-app-purchase/) and [App Review Guidelines](https://developer.apple.com/app-store/review/guidelines/)
- [Account binding with appAccountToken](https://developer.apple.com/documentation/storekit/product/purchaseoption/appaccounttoken(_:))
- [Unfinished transactions](https://developer.apple.com/documentation/storekit/transaction/unfinished) and [finishing delivery](https://developer.apple.com/documentation/storekit/transaction/finish())
- [Current entitlements](https://developer.apple.com/documentation/storekit/transaction/currententitlements), [explicit restore](https://developer.apple.com/documentation/storekit/appstore/sync()) and [subscription management](https://developer.apple.com/documentation/storekit/appstore/showmanagesubscriptions(in:))
