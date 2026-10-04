# Privacy, age rating and review answers

Source audit: 2026-10-05; saved App Store draft status last checked 2026-10-01. These answers describe the checked-out mobile client, its StoreKit module and the Solar server. They do not establish the hosting operator's actual logging, vendor contracts, retention, or Apple approval. Recheck against the final submitted binary and deployed configuration. No real passwords, private review phone numbers, transaction payloads or customer readings belong in this document.

Build `1.0.0 (9)` implements a one-calendar-month trial for newly created accounts and subscription access enforced on the server. StoreKit purchasing requires configured backend Apple billing. On 2026-10-05, the native StoreKit harness passed 11/11 tests, the full native Expo app and subscription bridge compiled, and the signed device archive was generated. Upload and Apple processing are pending. The saved App Store metadata and privacy questionnaire require synchronization before submission; this source draft does not publish those answers or establish live Apple purchase readiness.

## App Privacy: supported answers

**Do you or your third-party partners collect data from this app? Yes.** The server retains account/session identifiers and installation configuration submitted by the app. Having no advertising SDK does not make this a no-data-collected app.

For the following seven data types, use these follow-up answers:

| App Store Connect question | Answer supported by the implemented use |
| --- | --- |
| Purpose | **App Functionality** |
| Linked to the user's identity? | **Yes** |
| Used for tracking? | **No**, subject to the operator confirming that deployed partners do not use it for advertising measurement, targeted advertising or data-broker sharing |
| Additional purposes | Do not select advertising, marketing, analytics, personalization or other purposes unless the deployed operation actually uses the data that way |

| Data type to select | Source evidence and scope |
| --- | --- |
| **Contact Info > Email Address** | Verified email registration/sign-in and integration configuration may send an email address to the server. Identity persists account email; provider configuration is saved through the authenticated `/api/v2/integrations` API. Email has a specific category and should not be hidden under generic content |
| **Contact Info > Phone Number** | Verified phone registration/sign-in and account identity linking submit and retain the verified number in the account record |
| **Identifiers > User ID** | Sign-in uses username/email. `MobileSessionStore` retains user ID, username and token until expiry or revocation; Identity persists account records. Native session storage also contains username, although its local storage alone is not the reason for this declaration |
| **Identifiers > Device ID** | Inverter and smart-socket identifiers are submitted in selection/configuration and retained with installation data. This describes connected-equipment IDs, not collection of the phone's advertising identifier |
| **User Content > Other User Content** | Users create and edit rule names and related configuration; rule records persist. These text fields have no public social feed |
| **Other Data > Other Data Types** | Saved thresholds, schedules, polling/integration settings and other system configuration are retained. The service also retains installation energy/device observations; identify these explicitly in the policy, rather than claiming the service only stores login details |
| **Purchases > Purchase History** | The app sends Apple-signed subscription transactions to the authenticated server. The server retains account trial dates, product and transaction identifiers, an account token shared with Apple, expiry/revocation dates and verified subscription status; it refreshes status through Apple and Server Notifications V2 |

These data remain associated with identifiable accounts and installations. A newly created account receives its own installation; access to an installation requires explicit membership and permissions. Additional members can share authorized access. Tenant-scoped storage does not anonymize those records.

Apple distinguishes retained off-device collection from local processing and transient requests. The label must include qualifying partner collection and its actual purposes. [Apple App Privacy Details](https://developer.apple.com/app-store/app-privacy-details/).

## Categories requiring care

| Data type | Current source conclusion | When to change the answer |
| --- | --- | --- |
| **Financial Info > Payment Info** | **No** in current source. App Store billing does not expose card/bank details to this client or Solar server | Change if the operator starts receiving or retaining payment details through another implemented flow |
| **Financial Info > Other Financial Info** | **No off-device personal financial collection from the app found.** The server retains energy observations and public electricity-price rows; revenue/deposit totals are computed for each API response. No personal invoice, payout account or revenue ledger is persisted by this code | Reassess if deployed logs, exports, invoices or billing features retain users' financial results. Displaying a computed value alone is not evidence of retained collection |
| **User Content > Customer Support** | **Operator confirmation needed.** Support is via the approved public email; there is no in-app support-message form or automatic diagnostic upload | Include retained app-related support submissions when applicable and not eligible for optional disclosure. Confirm what is retained and whether it can be tied to the sender/account |
| **Diagnostics > Other Diagnostic Data** | **Operator confirmation needed.** No mobile crash-reporting SDK found, but server/proxy/security logging is outside the mobile dependency list | Include retained technical information originating from app requests, such as IP/request logs, under the category matching its actual use. App Functionality is appropriate for service/security operation; add Analytics only if actually used for behavior analysis |
| **Location / Physical Address** | The app does not request phone GPS. Solar-site settings accept latitude, longitude and a location label; these values are submitted to the authenticated server and retained in installation settings | Classify the submitted installation coordinates under the location category matching their actual precision, and include qualifying partner use by the weather service. Local timezone selection alone does not collect GPS coordinates |
| **Usage Data > Product Interaction** | No tap/screen-event analytics upload found. Rule execution logs describe installation operation | Reassess if deployed analytics or server logs retain identifiable app interaction events for that purpose |

Do not add speculative categories merely because a third-party SDK is present. Conversely, do not omit known server or partner collection. Apple's own collection and information processed only on the device are treated separately from the developer's retained collection. [Apple App Privacy Details](https://developer.apple.com/app-store/app-privacy-details/).

## Policy and retention facts

Public URLs are `https://solar.dshapar.com/privacy` and `https://solar.dshapar.com/support`; both are saved in their App Store Connect fields. The Razor pages exist in this branch, but deployment and public accessibility must be verified before submission. The final app includes privacy/support links. Public contact: `dmitry.shapar@gmail.com`. The collection questionnaire, its Publish action and the age questionnaire remain pending.

The policy should match these source facts:

- The login password is not persisted by the mobile app; its iOS session uses Keychain. Changing server/signing out clears that session.
- Connection credentials saved through the provider-driven Integrations settings are retained by the server for the configured manufacturers. Logging out does not remove them or delete the server account.
- Ordinary readings have a 31-day cleanup window and rule execution logs a 3-day cleanup window in `RuleRunHistory`; cleanup can be delayed by operational failures. This is not a guarantee about backups or external logs.
- Identity, saved settings, rules, export observations and market-price rows have no automatic expiry implemented. Operator deletion and backup retention must be described accurately.
- Configured inverter/socket cloud providers, Open-Meteo, PSE and hosting/network providers participate in the service. Open-Meteo receives server-configured installation coordinates; the mobile app does not request phone GPS.
- StoreKit purchase records are sent to Solar for authenticated verification and account binding. Signed payloads are not persisted in ordinary client storage. Server purchase identifiers and verified status have no automatic deletion period implemented; operator retention and deletion policy must cover them.

The app has verified email/phone registration and Google account sign-in/linking. More > Settings & account provides password change, session revocation, account-data export and account deletion, confirmed by the current password or a fresh verification code. Account deletion removes identity, billing and session records plus exclusively owned installation data. An owner with other installation members must transfer ownership first; outstanding device commands must be resolved before deletion. Operator backup retention and the external support contact route still need an accurate policy.

## Age-rating questionnaire

Use the following content answers for the current native screens and their sample data. Apple calculates the final global and regional ratings from the questionnaire; **4+ is the expected global result**, not a manually guaranteed rating. The app is a Utilities app, not a Kids Category submission. [Set an age rating](https://developer.apple.com/help/app-store-connect/manage-app-information/set-an-app-age-rating/).

| Control / capability | Answer |
| --- | --- |
| Parental Controls | No |
| Age Assurance | No |
| Unrestricted Web Access | No |
| User-Generated Content | No |
| Social Media | No |
| Social Media Disabled for Users Under 13 | Not applicable / No, if asked |
| Messaging and Chat | No |
| Advertising | No |

| Content descriptor | Frequency / presence |
| --- | --- |
| Profanity or Crude Humor | None |
| Horror / Fear Themes | None |
| Alcohol, Tobacco, or Drug Use or References | None |
| Medical or Treatment Information | None |
| Health or Wellness Topics | None / No |
| Mature or Suggestive Themes | None |
| Sexual Content or Nudity | None |
| Graphic Sexual Content and Nudity | None |
| Cartoon or Fantasy Violence | None |
| Realistic Violence | None |
| Prolonged Graphic or Sadistic Realistic Violence | None |
| Guns or Other Weapons | None |
| Simulated Gambling | None |
| Gambling | No |
| Contests | None |
| Loot Boxes | No |

Private rule names/settings do not constitute broad public UGC distribution. Explicit links open the external browser; there is no embedded general browser. StoreKit subscriptions are purchases, not gambling or randomized loot. Answer **Yes** to an in-app-purchases question if one is shown elsewhere. A login requirement is not age assurance, and the system's purchase controls are not a custom parental-control feature. If the operator imposes a higher minimum age through its terms, apply that accurate override. [Age-rating definitions](https://developer.apple.com/help/app-store-connect/reference/app-information/age-ratings-values-and-definitions/).

## Review notes and access

The currently saved credential-free review notes are in [metadata.md](metadata.md). Try demo is integrated and exposes fictional data in the native installation screens. Sign-in required is off; the owner's supplied review contact is saved in Apple's private fields. Production credentials and the private contact phone remain outside the repository.

The saved paid-flow notes must match the final tested billing binary. No separate Login subscription-review entry has been implemented by this task. The demo stays offline; connected access requires server sign-in and a valid trial or subscription. Provide functional purchase review access with an isolated account: an informational preview that only redirects to sign-in does not let Apple complete the configured purchase flow.

Apple's checklist accepts a complete demo mode. Guideline 2.1(a) also retains a prior-approval condition when replacing a demo account for legal/security reasons. Do not claim this draft resolves that condition: disclose the offline mode and real-equipment behavior, and obtain the applicable review-access agreement or supply a dedicated suitable account. Purchase review must also remain possible. [App Review Guidelines](https://developer.apple.com/app-store/review/guidelines/).

## Deferred public-submission facts still needed

These apply when App Store publication resumes, rather than as new requests for the current TestFlight work.

1. **Deployment and operation:** confirm that the new public pages are deployed; identify hosting/proxy/security providers, retained IP/request/response logs, their uses, retention and backups. Confirm no advertising/data-broker use by the operator or partners before finalizing No tracking.
2. **Privacy operations:** operator/legal identity and working access/correction/deletion process; backup retention and the installation membership/permission policy. Confirm treatment of support emails and whether financial result exports or other retained records exist outside this source.
3. **Review access:** Apple's applicable agreement to the demo-only hardware approach, or suitable isolated dedicated access if requested. Review contact details have already been supplied and saved; do not request them again. Do not put production equipment credentials in a public document.
4. **Commercial account setup:** active Paid Apps Agreement, the owner's accurate bank/tax/trader information and any required territory-specific documents. Previously configured U.S. base prices are USD 4.99 monthly / USD 29.99 yearly in one subscription group. Remove the old two-week introductory offers; the Solar server provides the account's one-month trial. Verify Apple's sandbox catalog and final contract status before submission. ASC previously showed a non-trader declaration; the account holder must determine whether it remains accurate for paid distribution.
5. **Connected-provider commercial configuration:** the current owner decision is to keep the free API arrangement for TestFlight. Before any future commercial release, confirm the permitted Open-Meteo arrangement and privately configure a paid API key if required. Open-Meteo excludes commercial use from its free tier and places historical/satellite APIs on Professional or higher plans. CC BY attribution already shown by the app does not establish a commercial API-service licence. [Open-Meteo pricing](https://open-meteo.com/en/pricing).

These are missing operational/account facts, not reasons to repeat permissions already given for testing, uploading or publication. Existing verified values in App Store Connect can supply the relevant account fields without requesting them again.
