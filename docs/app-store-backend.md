# App Store subscription backend

The iOS subscription flow uses StoreKit 2 auto-renewable subscriptions. Apple Pay is a separate payment mechanism; it is not used for these App Store subscriptions. The server is the authority for trial and paid access. A purchase sheet, local StoreKit entitlement, client date or unsigned transaction identifier cannot unlock the API.

## Operator configuration

Keep `Billing:Apple:Enabled` false until App Store Connect products, keys and sandbox verification are complete. Disabled purchasing does not extend the server trial. A disabled or unconfigured Apple backend returns a retryable error and never grants paid access.

Set these values in deployment environment configuration, before the SQL settings provider is loaded. Do not save them in editable application settings:

Start from [billing.env.example](billing.env.example). Enabled configuration validates the signing key and trusted certificate contents at startup. The example keeps purchasing disabled and contains no credentials.

```text
Billing__Apple__Enabled=false
Billing__Apple__BundleId=com.dshapar.solar
Billing__Apple__Environment=Sandbox
Billing__Apple__AppAppleId=<numeric App Store app ID, required in Production>
Billing__Apple__IssuerId=<App Store Connect issuer UUID>
Billing__Apple__KeyId=<10-character In-App Purchase key ID>
Billing__Apple__PrivateKeyPath=<absolute path to the In-App Purchase .p8 key>
Billing__Apple__RootCertificatePaths__0=<absolute path to AppleRootCA-G3.cer>
Billing__Apple__ProductIds__0=com.dshapar.solar.monthly
Billing__Apple__ProductIds__1=com.dshapar.solar.yearly
```

Download the trusted root directly from [Apple PKI](https://www.apple.com/certificateauthority/), rather than from a transaction payload. Store the public certificate and private key in an operator-controlled directory outside the editable application content. Grant the application read access to the private key; do not commit it, log it or expose its directory through static hosting. Certificate revocation checks must reach Apple's CRL/OCSP services; the server also needs HTTPS access to `api.storekit-sandbox.apple.com` or `api.storekit.apple.com`.

Enable one environment per deployment. Sandbox signatures are never accepted by a Production deployment, and failed Production verification does not silently retry in Sandbox. Use a separate sandbox deployment/database for test Apple identities and transactions. App Review can exercise sandbox purchases in a production-signed binary: provide the reviewer with the sandbox server address and its Solar test account, using the app's server setting before sign-in. Before switching an existing deployment between environments, arrange account/subscription cutover: stored subscriptions from a different environment cannot grant access or change ownership. Apple's sandbox may omit or return zero for the numeric app ID; bundle identity, environment, certificate trust and account binding still apply. Production requires the configured numeric app ID.

In App Store Connect, create the configured auto-renewable products in one subscription group and obtain an In-App Purchase signing key under Users and Access > Integrations > In-App Purchase. Configure the monthly period as one month; the server trial is one calendar month from account creation and allows one socket. An Apple introductory offer is an independent App Store offer and does not reset the account's server trial. The server does not hardcode product prices. Retain localized StoreKit prices and renewal disclosures in the app.

Set the App Store Server Notifications **V2** URL to:

```text
https://<server>/api/billing/apple/notifications
```

Set both production and sandbox notification destinations to the corresponding deployment. Send an Apple test notification and verify the accepted response on the sandbox deployment before enabling purchasing. HTTPS/TLS and valid publicly trusted certificates are required.

## API and persistence contract

`GET /api/billing/access` returns the signed-in account's authoritative trial or subscription state and its stable UUID `appAccountToken`. The app must supply that token in the StoreKit purchase option. `POST /api/billing/apple/verify` accepts `{ "signedTransaction": "<StoreKit transaction JWS>" }` with a valid Solar mobile bearer. It validates the signature, trusted Apple certificate chain and certificate roles, bundle, product, environment and account token, then fetches current status from Apple's [Get All Subscription Statuses](https://developer.apple.com/documentation/appstoreserverapi/get-all-subscription-statuses) API. The mobile app finishes the StoreKit transaction only after this request succeeds.

The response includes `serverNow` and nullable `accessValidUntil`. This access deadline combines the remaining server trial with the paid subscription's verified cache lifetime. `subscriptionExpiresAt` remains Apple's actual paid or grace expiration for disclosures. Paid access ends at the earlier of that expiration and the one-hour status trust cutoff; an eligible remaining trial can keep account access until its own deadline, with the trial's one-socket quota. Expired access returns a null access deadline. Clients use the access deadline to close cached private content even if a later server refresh is unavailable.

Subscriptions without the server's matching account token cannot be claimed through this endpoint, including transactions made by older builds without account binding. Restore on a different Solar account is denied. The original transaction has one persisted owner; token and owner checks happen before repeat/idempotent early returns. Deleting an account removes its billing rows; a recreated account receives a new token and cannot claim transactions signed with the deleted account's token.

Every authoritative status update is one SQL transaction. A newer observation or Apple signing date fences a slower/older response. Duplicate requests do not extend expiration or create duplicate records. Revoked/refunded transactions and billing retry do not grant access. Billing grace access lasts only until Apple's signed grace expiration. A canceled auto-renewal retains access through its already-paid expiration.

Disable Apple's free introductory offers: the account already receives its free calendar month from the server, without buying a subscription. If an Apple-signed transaction nevertheless declares `offerDiscountType: FREE_TRIAL` or an explicit zero `price`, it cannot grant paid access, expand the one-socket trial quota or extend the server trial deadline. The signed price is optional for older transactions; a missing price does not reject an otherwise valid legacy purchase. A subsequent paid renewal replaces the free-offer flag and can grant paid access after verification. Keep Family Sharing disabled unless an explicit Solar account ownership policy is implemented; this backend requires each transaction to carry the purchasing Solar account token.

Signed notification data is verified before processing and is treated as an invalidation signal. The server fetches current Apple status rather than applying notification order directly. It acknowledges notifications only after persistence succeeds; temporary verification, transport and database failures return non-success so Apple can retry. Unknown or deleted account tokens are acknowledged without creating or changing accounts. Raw signed transaction or notification payloads are not stored or logged.

The reconciliation worker refreshes known subscriptions at startup and every 15 minutes. Cached paid access expires after one hour without a successful verified refresh, or at the subscription/grace expiration, whichever comes first. Transient transport, timeout and Apple service failures retain the last verified record but cannot refresh its age. A successful Apple API response with missing, foreign, malformed or unverifiable status immediately invalidates the matching account's cached grant; a later verified response can restore trust. The invalidation shares the transaction ownership and observation-order fences, so an old failed response cannot close a newer verified subscription. A malformed or foreign client receipt never triggers this server-status invalidation.

## Database cutover and recovery

The `AccountBilling` migration gives all existing accounts the same SQL UTC cutover instant and one calendar month of trial access from that instant. Each account receives a distinct stable Apple account token. Existing socket bindings, settings and history remain intact, including installations that already contain multiple sockets. Fresh installation, historical upgrade and repeated application are covered by maintained SQL Server migration tests.

New socket selections record the authenticated adding account in `AddedByUserId`. Trial quota counts that account's attributed socket bindings across installations. Historical bindings have no reliable adding-account history, so unassigned bindings conservatively count for each current member of their installation. Existing selection can be repeated without consuming another slot; selecting another socket during trial requires the account to have no counted socket. A verified paid subscription removes that account's trial quota.

Shared installation automation may run while any eligible installation member has trial or paid access. Interactive reads and commands require the acting person's own account access; another member's subscription cannot unlock an expired person's account. If every installation member loses access, socket reads, commands and automated cycles stop.

Before applying the migration, take a full database backup and verify restoration in an isolated environment. Preserve the prior application image and the associated data-protection/integration keys with the recovery material. Restoring billing ownership, trial dates and private data requires a coordinated database/application recovery, rather than assuming an older image alone rolls back the schema or billing state.

The migration's downgrade guard raises SQL error `51000` when billing records exist: dropping those records would reset trials and lose Apple subscription ownership history. An empty billing schema can downgrade and reapply; populated billing recovery requires the verified coordinated backup. Keep backups and credentials outside the repository and editable application content.

## Required external validation before paid release

Local tests cover generated certificate chains and forged signatures, account/token isolation, SQL transaction ownership, duplicate and concurrent refreshes, expiration, grace, refund/revocation, stale callbacks and API failures. These use synthetic Apple data and do not demonstrate App Store Connect availability, real Apple certificate revocation service compatibility or a successful Apple payment.

On a signed iOS sandbox build, verify product availability and localized disclosures, server-token purchase, pending approval, cancellation, receipt upload retry before transaction finish, renewal, restore on the original Solar account, rejected restore on another account, natural expiration, configured billing grace, refund, notification retries, account deletion and app restart/foreground recovery. Check each result through the real API and an independent database read, with an unaffected neighboring Solar account. Confirm Apple's real signed certificate chain works with online revocation checks on the deployment OS. Switch Production only after production products and keys are ready; publish after final checks of the actual deployed version.

References: [Apple's App Store Server Library verification implementation](https://github.com/apple/app-store-server-library-python/blob/main/appstoreserverlibrary/signed_data_verifier.py), [Apple notification status values](https://developer.apple.com/documentation/appstoreservernotifications/status), [Apple-signed transaction payload](https://developer.apple.com/documentation/appstoreserverapi/jwstransactiondecodedpayload), [Apple signed transaction price](https://developer.apple.com/documentation/appstoreserverapi/price).
