# DeyeSolar App Store preparation

Updated on 2026-10-01 from the mobile/server source and the saved App Store Connect draft. The app is **Prepare for Submission**; saving metadata or configuring products does not constitute Apple approval or publication. Keep review passwords, private review phone numbers, signing credentials, transaction payloads and production telemetry out of this repository.

**Owner decision on 2026-10-01: defer public App Store submission and finish TestFlight first, using the free weather API configuration.** App Store editing, screenshot upload, build selection and review submission are paused. The TestFlight release is being prepared with subscription enforcement disabled and full connected access after server sign-in; root must verify that flag in the final archive. The paid description, review notes and products below are retained preparation for a later App Store release, not the terms of the current TestFlight plan.

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
| Future App Store business model | Prepared auto-renewable subscriptions, 14-day trial for eligible users; configured U.S. base prices USD 4.99/month and USD 29.99/year. TestFlight plan uses no required subscription |
| Review contact | Dmytro Shapar, approved public support email; owner-supplied phone entered only in Apple's private contact field |
| Future App Store review access | Try demo is integrated; Sign-in required is off and credential-free review notes are saved. Resolve Apple's review-access caveat and functional subscription review access when resuming paid submission |
| EU trader status | ASC currently displays an existing non-trader declaration. The account holder must confirm that it remains accurate for paid distribution |
| Release setting | Existing default Automatically release this version after approval; not changed by this preparation |

Apple limits the name and subtitle to 30 characters. The support and privacy URLs are required. [App information](https://developer.apple.com/help/app-store-connect/reference/app-information/app-information/), [version information](https://developer.apple.com/help/app-store-connect/reference/app-information/platform-version-information/).

## Description

Saved English (U.S.) description (2,284 characters):

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
Choose a monthly or annual subscription through the App Store for connected access. Eligible customers can start with a 14-day free trial. After the trial, the selected plan renews automatically at its displayed price unless cancelled. The U.S. prices are $4.99 per month or $29.99 per year; prices in other regions are shown in your local currency in the app and may vary.

Payment is charged to your Apple Account. Cancel at least 24 hours before the current trial or billing period ends to prevent automatic renewal. Manage or cancel your subscription in your Apple Account settings. Trial eligibility is determined by Apple; only one introductory offer is available per subscription group.

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

Credential-free notes were saved in App Store Connect during paid-release preparation. The current demo reviews the installation screens; the retained purchase text describes the prepared gated build and is not valid for TestFlight with its gate disabled. Update these notes to match the future submitted App Store binary before resuming review. No separate Login subscription-review path has been implemented by this task.

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
After sign-in without an active verified entitlement, Solar Premium presents Monthly and Yearly plans. Select a plan and tap Start 14-day free trial when Apple confirms eligibility, or Subscribe otherwise. Restore purchases is available on that screen. With active access, More > Subscription opens subscription status and restore/manage actions. Both products provide the same connected monitoring and control features in one group (Solar Premium): com.dshapar.solar.monthly, U.S. $4.99/month; com.dshapar.solar.yearly, U.S. $29.99/year. Each has a 2-week free introductory offer for eligible users; Apple controls eligibility. Prices are localized by StoreKit.

The offline demo bypasses paid connected access. The native purchase and entitlement flows have been tested with Xcode StoreKit configuration. Apple sandbox/TestFlight verification and final product review remain separate checks.

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

The source-based answers and outstanding operator facts are in [privacy-and-review.md](privacy-and-review.md). The expected retained categories are Email Address, User ID, connected-equipment Device ID, Other User Content and Other Data Types, for App Functionality, linked to the user. Tracking is No only after confirming actual deployed partner purposes. Local StoreKit processing alone does not establish developer collection of Purchase History; retained server/proxy/support data still require an operational audit.

The public policy URL is saved in ASC, but **the collection questionnaire and Publish have not been completed by this preparation**. The age-rating questionnaire is also pending; the documented content answers expect global 4+, subject to Apple's computed regional results. Having no advertising/analytics SDK or an empty privacy-manifest collection array does not establish no data collection. [App Privacy Details](https://developer.apple.com/app-store/app-privacy-details/).

## Trial and paid access

StoreKit 2 is prepared and local Xcode StoreKit testing has passed. It is retained for a future paid App Store release; the current TestFlight plan disables required subscriptions. The monthly/yearly products below are configured in ASC, but contract readiness, Apple sandbox verification and review remain separate requirements. Both products belong to **Solar Premium, group ID `22429477`**, with English (U.S.) display name Solar Premium and app name DeyeSolar. They occupy the same level, 1, because they provide equal service. [Subscription setup](https://developer.apple.com/help/app-store-connect/manage-subscriptions/offer-auto-renewable-subscriptions/).

| Subscription field | Monthly plan | Annual plan |
| --- | --- | --- |
| Reference name | Solar Premium Monthly | Solar Premium Yearly |
| Apple product ID | `6817928193` | `6817930937` |
| Product ID | `com.dshapar.solar.monthly` | `com.dshapar.solar.yearly` |
| Duration | 1 month | 1 year |
| Configured U.S. base price | USD 4.99 | USD 29.99 |
| Introductory offer | Free, 2 weeks | Free, 2 weeks |
| Group | Solar Premium | Same group |
| Level | 1 | 1 |
| English display name | Solar Premium Monthly | Solar Premium Yearly |
| English description | Confirm root's saved monthly localization | Full access to solar monitoring and automation. |
| Availability | All 175 regions, owner requested | All 175 regions, automatic future-region availability selected |
| Regional prices | Automatic prices for 175 regions | Automatic prices for 175 regions |
| Introductory offer dates | Configured by root; verify final effective dates | Sep 30, 2026, no end date, all 175 regions |

Two-week free trials are supported for both durations. Eligibility is shared across the group, so switching plans does not create another trial. Check eligibility before advertising a trial in the app. [Introductory offers](https://developer.apple.com/help/app-store-connect/manage-subscriptions/set-up-introductory-offers-for-auto-renewable-subscriptions/).

Still complete product review screenshots/notes and any missing monthly localization. Select both initial subscriptions and their group for review alongside the app version only when ready. Verify purchases, restoration, renewal, cancellation, expiration and trial eligibility in Apple's sandbox before submission. Local Xcode fixtures do not prove Apple's live sandbox catalog or paid-contract readiness.

The Account Holder must accept the separate Paid Apps Agreement for in-app purchases. Complete the required banking and tax information in Business; an accepted Developer Program agreement alone does not establish paid-contract readiness. [Agreements](https://developer.apple.com/help/app-store-connect/manage-agreements/sign-and-update-agreements/), [banking](https://developer.apple.com/help/app-store-connect/manage-banking-information/enter-banking-information/), [tax information](https://developer.apple.com/help/app-store-connect/manage-tax-information/provide-tax-information/).

The prepared paid flow gates connected iOS screens on a verified active local StoreKit entitlement; this gate is being disabled for the TestFlight release. Offline demo exploration is free. The current server does not receive signed purchase records or enforce the subscription at API level. When resuming paid release work, audit entitlement/account scope and explain it accurately; do not advertise server-verified per-installation entitlement that is not implemented.

## Deferred App Store submission work

These requirements apply when the owner resumes public App Store publication. Do not complete them as part of the current TestFlight-only scope.

1. Select the processed signed release build and finish device/TestFlight stability checks.
2. Complete Paid Apps Agreement, banking/tax requirements and Apple sandbox subscription verification. Finish subscription review assets and synchronize purchase-access instructions with the final binary.
3. Deploy and verify the support/privacy pages, confirm actual retention/deletion/logging and publish truthful App Privacy answers.
4. Resolve Apple's demo-access caveat and provide functional review access to purchases; an informational preview alone does not permit a reviewer to complete a purchase.
5. Upload fictional-data screenshots at the required iPhone/iPad sizes. Root is preparing captures; none were uploaded by this metadata task.
6. Complete age rating, content rights, export compliance and app pricing/availability. Name, subtitle, category, version, copyright, description and review contact are saved.
7. Have the account holder confirm the existing trader declaration and territory-specific requirements for worldwide paid availability. [EU trader requirements](https://developer.apple.com/help/app-store-connect/manage-compliance-information/manage-european-union-digital-services-act-trader-requirements/).
8. Add the finished app and both initial subscriptions for review and submit when all requirements are met. No Add for Review, Submit or Publish action was taken by this preparation. Public release follows Apple approval and the chosen release option. [Submit an app](https://developer.apple.com/help/app-store-connect/manage-submissions-to-app-review/submit-an-app/).
