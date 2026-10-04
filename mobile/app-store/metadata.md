# DeyeSolar App Store preparation

Updated on 2026-10-01 from the mobile/server source and the saved App Store Connect draft. The app is **Prepare for Submission**; saving metadata or configuring products does not constitute Apple approval or publication. Keep review passwords, private review phone numbers, signing credentials, transaction payloads and production telemetry out of this repository.

**Source update on 2026-10-04:** the billing implementation now gives Solar accounts a one-calendar-month trial, limits new trial socket selection to one, and enforces reading/control access on the server. Existing accounts receive the month from billing migration. App Store purchasing remains disabled until the operator configures the backend Apple credentials and products. The saved draft below is historical: its two-week introductory offers and local-only entitlement description must be replaced before submitting the billing build. Remove those Apple introductory offers to avoid advertising a second free trial. Billing build `1.0.0 (9)` is prepared in source after the previously uploaded Google-linking build 8. Run the current native StoreKit harness and live Apple sandbox checks before building and uploading it. No App Store Connect metadata or publication action is performed by changing this file.

The previous 2026-10-01 scope deferred App Store submission and kept TestFlight access free. The 2026-10-04 request resumes billing work and permits deployment/publication only after it works and all required checks pass. This source document prepares that release; native compilation and Apple sandbox checks remain outstanding on the current Windows host.

## App information

| Field | Value or status |
| --- | --- |
| Name | DeyeSolar — created in App Store Connect |
| Apple app ID | `6817925250` |
| Subtitle | Solar monitoring and control — saved |
| Primary language | English (U.S.) |
| Primary category | Utilities — saved |
| Bundle ID | `com.dshapar.solar` |
| Version | `1.0.0` |
| Build number | Not selected in the version card; select the final processed release build after the current archive/upload work |
| Support email | `dmitry.shapar@gmail.com` — approved for public support |
| Support URL | `https://solar.dshapar.com/support` — saved in version card; verify deployed page before review |
| Privacy Policy URL | `https://solar.dshapar.com/privacy` — saved in App Privacy; verify deployed page before review |
| Copyright | 2026 Dmytro Shapar — saved |
| Availability | All available countries and regions, as requested by the owner; complete applicable territory requirements |
| Billing business model | One-calendar-month Solar account trial, one newly selected socket during trial, then server-verified App Store subscription access. Previously configured U.S. base prices USD 4.99/month and USD 29.99/year; verify before submission |
| Review contact | Dmytro Shapar, approved public support email; owner-supplied phone entered only in Apple's private contact field |
| Future App Store review access | Try demo is integrated; Sign-in required is off and credential-free review notes are saved. Resolve Apple's review-access caveat and functional subscription review access when resuming paid submission |
| EU trader status | ASC currently displays an existing non-trader declaration. The account holder must confirm that it remains accurate for paid distribution |
| Release setting | Existing default Automatically release this version after approval; not changed by this preparation |

Apple limits the name and subtitle to 30 characters. The support and privacy URLs are required. [App information](https://developer.apple.com/help/app-store-connect/reference/app-information/app-information/), [version information](https://developer.apple.com/help/app-store-connect/reference/app-information/platform-version-information/).

## Description

Replacement English (U.S.) description for the billing release, to synchronize with App Store Connect after verification:

```text
DeyeSolar connects you to your Solar installation so you can follow your energy data and manage configured smart sockets from your iPhone or iPad. Tap Try demo to explore the app with fictional data before connecting an installation.

See your energy status
Check battery charge, battery power and grid power from the Home dashboard. Refresh readings when you need an update.

Explore solar generation
Compare actual generation with the server's estimated range. Browse generation history for today, the week or the month.

Follow electricity export estimates
View export energy and estimated revenue by period. Completed-period figures and the provisional current hour are shown separately. Estimates depend on your server's available data and pricing configuration; they are not a guaranteed settlement amount.

Manage your connected installation
View smart socket status, send manual on/off commands and create or adjust automation rules. Review inverter readings and automation run history. Configure supported cloud integrations and your display timezone.

Connected mode requires an existing Solar server account, an internet connection and a configured compatible installation. Enter your Solar server URL on the sign-in screen to connect. Available readings and controls depend on that server's integrations and configuration. The offline demo uses sample data and does not control real equipment.

Subscription access
Your Solar account starts with a free trial lasting one calendar month. You can add one smart socket during the trial. After the trial ends, an active subscription is required to read or control your sockets. Existing accounts receive one month from the server billing update.

Choose a monthly or annual subscription through the App Store. The server verifies the purchase and binds it to your Solar account. The selected plan renews automatically at its displayed price unless cancelled. Prices are shown in your local currency before purchase and may vary by region.

Payment is charged to your Apple Account. Cancel at least 24 hours before the current billing period ends to prevent automatic renewal. Manage or cancel your subscription in your Apple Account settings. The Solar account trial starts without a purchase and does not automatically charge you.

Support: dmitry.shapar@gmail.com
Privacy Policy: https://solar.dshapar.com/privacy
Terms of Use: https://www.apple.com/legal/internet-services/itunes/dev/stdeula/
```

Verify that the final submitted binary and Apple's sandbox catalog match these terms. Xcode StoreKit testing is distinct from an actual Apple sandbox/TestFlight purchase.

## Keywords

```text
energy,battery,inverter,photovoltaic,generation,grid,export,automation,socket,monitoring
```

## Promotional text

```text
Follow battery and grid readings, explore solar generation and export estimates, and manage your configured smart sockets and automation rules.
```

The description limit is 4,000 characters, keywords 100 bytes, and promotional text 170 characters. [Version information](https://developer.apple.com/help/app-store-connect/reference/app-information/platform-version-information/).

## App Review notes

Credential-free notes were saved in App Store Connect during earlier paid-release preparation. The replacement below describes the current source; synchronize it with the final tested native build and provide functional purchase review access. No separate Login subscription-review path has been implemented by this task.

```text
DeyeSolar is a native client for an authenticated Solar installation. It displays battery and grid readings, generation history, server-calculated electricity export estimates, connected smart sockets, automation rules and run history.

Review the offline demo:
1. Open the app and tap Try demo on the sign-in screen. No account or server connection is required.
2. Home shows current sample readings and summary cards. Generation and Sales offer period controls and detail charts. Devices lists simulated sockets.
3. More opens Automation rules, Readings & run history, and Settings & account. You can switch simulated sockets, create/edit/enable/delete rules, discover fictional installations and edit sample settings.
4. Tap Exit demo to return to sign-in. Changes are held only in memory and discarded on exit.

The demo uses fictional data in the same native screens as connected mode. It sends no network requests and never operates real equipment or submits integration credentials. Sales figures are illustrative electricity-export estimates, not trading, payments or guaranteed settlements.

Connected mode requires an existing Solar server account and configured compatible hardware. It uses authenticated APIs and can operate actual equipment. Please consider the complete offline demo in lieu of a demo account and confirm prior approval under guideline 2.1(a), because exposing the owner's installation and live control credentials would create security risks. If additional isolated review access is required, contact us; no production login credentials are supplied in this submission.

Subscription navigation in connected mode:
After sign-in, a valid one-calendar-month Solar account trial opens connected access with a limit of one newly selected socket. More > Subscription shows the trial deadline and purchase actions. After expiry without a paid subscription, Solar Premium presents Monthly and Yearly plans, restore, policies and support. Select a plan and tap Subscribe. Purchases are verified by the Solar server and bound to the signed-in account before access starts. Both products provide the same connected features in one group (Solar Premium): com.dshapar.solar.monthly and com.dshapar.solar.yearly. Prices are localized by StoreKit. The Solar trial starts without a purchase; no separate Apple introductory trial is configured for this release.

The offline demo bypasses paid connected access. The current native purchase and receipt acknowledgement flows require the updated local Xcode StoreKit harness and Apple sandbox/TestFlight verification before submission. Local tests do not establish sandbox or product-review readiness.

Privacy Policy: https://solar.dshapar.com/privacy
Support: https://solar.dshapar.com/support
Terms: https://www.apple.com/legal/internet-services/itunes/dev/stdeula/
Review contact: dmitry.shapar@gmail.com
```

Apple's general checklist permits a complete demo mode, while 2.1(a) retains a prior-approval condition when replacing a demo account for legal/security obligations. The request in the notes does not establish that approval. Purchase review must also remain possible. [App Review Guidelines](https://developer.apple.com/app-store/review/guidelines/), [review fields](https://developer.apple.com/help/app-store-connect/reference/app-information/platform-version-information/).

## Screenshots

Capture the submitted app in use with a fictional account and an isolated demo installation. Do not publish the owner's telemetry, device serial numbers, account names, integration credentials or settings screenshots containing secrets. Proposed subjects: Home energy status, Generation history, Sales estimates, Devices, and Automation rules.

| Required group | Accepted portrait size to target |
| --- | --- |
| iPhone 6.9-inch display | 1320 × 2868, 1290 × 2796, or 1260 × 2736 |
| iPad 13-inch display, while iPad support is enabled | 2064 × 2752 or 2048 × 2732 |

One to ten opaque PNG/JPEG screenshots may be supplied per group. A 6.5-inch group can replace the 6.9-inch group. Existing 1206 × 2622 iPhone simulator captures belong to the 6.3-inch group and do not fill the larger required group. [Screenshot specifications](https://developer.apple.com/help/app-store-connect/reference/app-information/screenshot-specifications/).

Screenshots should demonstrate working features and use fictional account details. [App Review Guidelines, 2.3.3 and 2.3.9](https://developer.apple.com/app-store/review/guidelines/).

## Privacy and age answers

The source-based answers and outstanding operator facts are in [privacy-and-review.md](privacy-and-review.md). The expected retained categories are Email Address, User ID, connected-equipment Device ID, Other User Content, Other Data Types and Purchase History, for App Functionality, linked to the user. The app sends signed purchase records to the server, which retains verified subscription identifiers and status. Tracking is No only after confirming actual deployed partner purposes.

The public policy URL is saved in ASC, but **the collection questionnaire and Publish have not been completed by this preparation**. The age-rating questionnaire is also pending; the documented content answers expect global 4+, subject to Apple's computed regional results. Having no advertising/analytics SDK or an empty privacy-manifest collection array does not establish no data collection. [App Privacy Details](https://developer.apple.com/app-store/app-privacy-details/).

## Trial and paid access

StoreKit 2 is integrated with authenticated server verification in the current source. The earlier local nine-test harness passed before these changes; the updated 11-test harness and full Expo bridge build still require a current macOS run. The monthly/yearly products below were configured in ASC during previous preparation; contract readiness, Apple sandbox verification and review remain separate requirements. Both products belong to **Solar Premium, group ID `22429477`**, with English (U.S.) display name Solar Premium and app name DeyeSolar. They occupy the same level, 1, because they provide equal service. [Subscription setup](https://developer.apple.com/help/app-store-connect/manage-subscriptions/offer-auto-renewable-subscriptions/).

| Subscription field | Monthly plan | Annual plan |
| --- | --- | --- |
| Reference name | Solar Premium Monthly | Solar Premium Yearly |
| Apple product ID | `6817928193` | `6817930937` |
| Product ID | `com.dshapar.solar.monthly` | `com.dshapar.solar.yearly` |
| Duration | 1 month | 1 year |
| Configured U.S. base price | USD 4.99 | USD 29.99 |
| Previous introductory offer | Free, 2 weeks; remove before billing release | Free, 2 weeks; remove before billing release |
| Group | Solar Premium | Same group |
| Level | 1 | 1 |
| English display name | Solar Premium Monthly | Solar Premium Yearly |
| English description | Confirm root's saved monthly localization | Full access to solar monitoring and automation. |
| Availability | All 175 regions, owner requested | All 175 regions, automatic future-region availability selected |
| Regional prices | Automatic prices for 175 regions | Automatic prices for 175 regions |
| Introductory offer dates | Configured by root; verify final effective dates | Sep 30, 2026, no end date, all 175 regions |

The release uses the Solar account's one-month trial instead of an Apple introductory trial. Eligible extra App Store free-trial offers disable purchasing until the product configuration is corrected; the server also refuses to treat a free Apple offer as paid access or extend the account's trial. [Introductory-offer setup](https://developer.apple.com/help/app-store-connect/manage-subscriptions/set-up-introductory-offers-for-auto-renewable-subscriptions/).

Still complete product review screenshots/notes and any missing monthly localization. Select both initial subscriptions and their group for review alongside the app version only when ready. Verify purchases, restoration, renewal, cancellation, expiration and trial eligibility in Apple's sandbox before submission. Local Xcode fixtures do not prove Apple's live sandbox catalog or paid-contract readiness.

The Account Holder must accept the separate Paid Apps Agreement for in-app purchases. Complete the required banking and tax information in Business; an accepted Developer Program agreement alone does not establish paid-contract readiness. [Agreements](https://developer.apple.com/help/app-store-connect/manage-agreements/sign-and-update-agreements/), [banking](https://developer.apple.com/help/app-store-connect/manage-banking-information/enter-banking-information/), [tax information](https://developer.apple.com/help/app-store-connect/manage-tax-information/provide-tax-information/).

The current paid flow gates real connected screens on server-verified account access for every platform. The server enforces the trial deadline, socket limit and paid access at API boundaries. Native StoreKit supplies signed records for server verification and cannot unlock access locally. Offline demo exploration is free. Apple payment configuration and native/sandbox verification remain required before publishing the billing build.

## App Store submission prerequisites

Complete these requirements for the verified billing release before submission.

1. Select the processed signed release build and finish device/TestFlight stability checks.
2. Complete Paid Apps Agreement, banking/tax requirements and Apple sandbox subscription verification. Finish subscription review assets and synchronize purchase-access instructions with the final binary.
3. Deploy and verify the support/privacy pages, confirm actual retention/deletion/logging and publish truthful App Privacy answers.
4. Resolve Apple's demo-access caveat and provide functional review access to purchases; an informational preview alone does not permit a reviewer to complete a purchase.
5. Upload fictional-data screenshots at the required iPhone/iPad sizes. Root is preparing captures; none were uploaded by this metadata task.
6. Complete age rating, content rights, export compliance and app pricing/availability. Name, subtitle, category, version, copyright, description and review contact are saved.
7. Have the account holder confirm the existing trader declaration and territory-specific requirements for worldwide paid availability. [EU trader requirements](https://developer.apple.com/help/app-store-connect/manage-compliance-information/manage-european-union-digital-services-act-trader-requirements/).
8. Add the finished app and both initial subscriptions for review and submit when all requirements are met. No Add for Review, Submit or Publish action was taken by this preparation. Public release follows Apple approval and the chosen release option. [Submit an app](https://developer.apple.com/help/app-store-connect/manage-submissions-to-app-review/submit-an-app/).
