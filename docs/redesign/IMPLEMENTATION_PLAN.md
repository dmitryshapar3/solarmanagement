# SmartSolar redesign — Phase 0 implementation plan

Status: **Phase 0 completed and full implementation/release approved; see §12**. The discovery baseline below is historical. Discovery date: 6 October 2026, Europe/Warsaw.

Baseline: `main` at `2b6885a3214816d0067faf91786005be0ee5070a`. Planning branch: `feature/smartsolar-redesign`, created from that main commit. No commit, push, PR, deployment or App Store metadata change is part of Phase 0.

## 1. Scope, evidence and constraints

`CLAUDE_CODE_PROMPT.md` (all 162 lines) and `DESIGN_SPEC.md` (all 504 lines) were read completely. Discovery covered the current screens and endpoints in spec §7/§11, the design tokens and assets, and all 42 mockups opened through `design/index.html` in a browser: 24 mobile targets, 11 web targets, two design-system boards and five `Before-*` references. Inline styles were inspected alongside rendered copy. The five `Before-*` files are audit evidence, not implementation targets.

Architecture references reviewed: `docs/localization.md`, `solar-sales.md`, `solar-expected-power.md`, `accounts-deployment.md`, `app-store-backend.md`, `production-operations.md`, the test projects and `.github/workflows/tests.yml`. Current source takes precedence over outdated descriptions in those guides: for example, rule logs currently retain seven days, not the three still stated in `solar-expected-power.md`.

The approved design decisions remain fixed: an in-house web Razor UI library; removal of MudBlazor; manual-override choice; Light/Dark/System; user-visible SmartSolar naming; four mobile tabs and the new web routes. Database names, namespaces, bundle ID `com.dshapar.solar`, `deyesolar://` URLs, storage identities and existing API meanings remain unchanged. App Store listing metadata is outside this request.

The released build 11 remains a supported API client. New capabilities use new routes, optional fields or explicit opt-in query parameters. An omitted new parameter must preserve the current response shape and behavior. Installation data remains authorized per installation; account identity, billing and session controls remain authorized per user and reachable even without paid installation access.

Sample names, location, wattages, dates, provider models and prices are never production defaults. Missing facts stay null/unknown or are hidden. Demo fixtures may contain sample values but never call a real server. Production and export calculations remain distinct.

Notation in the following mappings: **W** = `src/DeyeSolar.Web/`; **M** = `mobile/src/`. Proposed filenames are implementation destinations, not claims that those files exist today. Replacement screens are deleted only after their routes, behaviors and tests have been ported.

## 2. Mockup-to-code mapping

### 2.1 Design system and shared foundations

| Mockup/source | Create or replace | Entry and scope |
|---|---|---|
| `Brand-Foundations.dc.html` | Create `scripts/generate-design-tokens.mjs`; generated `W/wwwroot/css/tokens.css` and `M/ui/theme/tokens.ts`; `M/ui/theme/ThemeProvider.tsx`; web appearance service/cookie; self-hosted font assets and brand assets | Shared light/dark tokens, typography, spacing, radii, shadows, wordmark and icons. Retire old theme constants only after callers migrate. |
| `Brand-Components.dc.html` | Create `W/Components/Ui/`, `W/Components/Charts/`, `M/ui/`, `M/ui/charts/`; `W/Pages/DesignGallery.razor` or a Development-only mapped gallery; `M/dev/DesignGalleryScreen.tsx` | Web `/_design` registered only in Development; mobile `DesignGallery` registered only under `__DEV__`. Every drawn state, plus both themes and accessibility states. |
| `assets/smartsolar-mark.svg`, `smartsolar-app-icon.svg` | Generate favicon SVG/PNG, apple-touch-icon and Expo/native iOS icon assets; update visible display names and permission copy | Keep native project, identifiers, URL schemes, subscription product IDs and localization references. Rasterize the supplied vectors deterministically; no new AI-generated logo. |
| Five `Before-*.dc.html` | No product implementation or deletion | Retain in the handoff as historical references. |

### 2.2 Web targets

The tenant app stays Blazor Server. Authentication and account-sensitive HTTP forms stay Razor Pages: this preserves antiforgery, redirects, cookie issuance, fresh proof and account access outside the paid installation gate. Both use the same tokens, sidebar/header styling and UI vocabulary. There is no framework rewrite.

| Mockup | Route | Files to create/replace/delete after port |
|---|---|---|
| `Web-SignIn` | `/signin` | Create `W/Pages/SignIn.cshtml` and `.cshtml.cs` with code/password steps and Apple/Google actions. Replace public auth layout/styles in `Pages/Shared/_PublicLayout.cshtml`, `wwwroot/css/auth.css`. Old `Login`, `Register`, `Verify` GETs become redirect adapters; keep necessary legacy POST handlers during transition. |
| `Web-Dashboard` | `/` | Replace `W/Pages/Index.razor`; create dashboard hero, flow, export summary, device/automation rows and recent-activity components. Remove the separate Manual Override block; reuse the command lifecycle through B7. |
| `Web-Energy` | `/energy` | Create `W/Pages/Energy.razor` and production presentation components. Port and retire `Generation.razor`, `SolarDetails.razor`, old `SolarHistoryChart.razor`/CSS once new SVG charts and tests replace them. Keep history/forecast services and provenance policy. |
| `Web-Energy-Export` | `/energy/export` | Create `W/Pages/EnergyExport.razor`; replace/port `Shared/SalesStatistics.razor`/CSS and `SalesQueryPageBase.cs`; retire `Sales.razor` and `SalesDetails.razor` after redirects. Reuse `ExportSalesService` and exact decimal calculations. |
| `Web-Devices` | `/devices`, `/devices/{id}` | Replace `W/Pages/Devices.razor`; create `Components/Devices/DeviceDetailPanel.razor`, settings/timeline/support details and manual-override dialog. Treat inventory IDs as escaped opaque strings; GUID-only command capabilities apply only to eligible dynamic devices. Replace the presentation of `Shared/SocketCommandControls.razor` while retaining its lifecycle service. |
| `Web-Automations` | `/automations`, `/automations/new`, `/automations/{id:int}` | Create `W/Pages/Automations.razor`, automation list/editor/evaluation/template components. Port and retire `Rules.razor`, `RuleEdit.razor`. Preserve configuration-version preconditions, required fields and source resolution. |
| `Web-Activity` | `/activity` | Create `W/Pages/Activity.razor`, filters/grouped table/summary components. Replace `RunHistory.razor`; retain legacy `/api/rule-runs` semantics. Include the CSV action drawn in the mockup. |
| `Web-Readings` | `/activity/readings` | Create `W/Pages/ActivityReadings.razor`; combine/port `History.razor`, `InverterDetails.razor` and shared reading/power-balance presentation. Keep raw/aggregate views, bounded pagination, gaps and CSV. |
| `Web-Settings` | `/settings` | Replace `W/Pages/Settings.razor` and `Shared/SiteSettingsEditor.razor`; create location/panel/tariff/refresh sections, TOC and one atomic save/discard bar. Move account preferences and integration management to their dedicated routes. |
| `Web-Connected` | `/settings/connections` | Create `W/Pages/Connections.razor` and `Components/Integrations/ConnectDialog.razor`; refactor `Integrations/IntegrationSettings.razor`, descriptor fields/discovery/selection/OAuth UI. Preserve admin-only packages UI as a collapsed disclosure. |
| `Web-Account` | `/settings/account` | Create `W/Pages/Settings/Account.cshtml` and `.cshtml.cs`, shared app-page layout/partials for plan/profile/preferences/methods/sessions/data. Port `Account.cshtml(.cs)` and `Billing.cshtml(.cs)` handlers; redirect old GETs. Keep `ChangeLanguage` and `POST /account/language`. |

Replace `W/Shared/MainLayout.razor`, `NavMenu.razor`, and theme/provider presentation in `App.razor`. Update `_Host.cshtml`, `_Imports.razor`, `Program.cs` and `Operations/ApplicationServices.cs` as screens migrate; the latter owns current Mud registration and cookie LoginPath. Remove Mud styles/scripts/providers/services/package only when no references remain. Preserve error boundaries, installation/session authorization, circuit checks and `Shared/AsyncQueryScope.cs` query lifetime handling.

Required web screens not drawn:

| Screen/state | Destination |
|---|---|
| Code verification/password fallback | Steps/handlers of `Pages/SignIn.cshtml`; preserve safe local return destinations. |
| Add service, check, discover, choose devices | `Components/Integrations/ConnectDialog.razor`, using the same three-step descriptor-driven model as mobile. |
| Privacy, Support, Error, 404 | Restyle existing `Pages/Privacy.cshtml`, `Support.cshtml`, `Error.cshtml` and `App.razor` NotFound; preserve `/privacy`, `/support` and error handling. |
| Delete rule/account, unlink identity, change password | Shared accessible confirmation/fresh-proof dialogs or account HTTP forms, with field-level validation. |
| Narrow layout | Responsive app bar/drawer below 1024px; single-column panels and internally scrolling tables at 390px. |
| Loading/empty/offline/error/partial/unknown result | Shared state components, exercised on every page, including after a Blazor reconnect. |

### 2.3 Mobile targets

Replace `M/application/navigationTypes.ts` and the navigator composition in `AppNavigator.tsx`; keep React Navigation 7 and native-stack. Signed-out navigation is separate from the authorized app. `MainTabs` contains Home, Energy, Devices and Automations. Home owns a nested native stack with Dashboard, LiveReadings and SettingsStack, so Live Readings and Settings retain the tabs shown in the references. Settings opens from Home's avatar; its child settings screens push within that stack. Full automation editors, Connect and Paywall use the root stack/modal flows, and quick controls use sheets. Preserve navigation memory above the subscription gate and reset it on API/account/session changes exactly as today. Foreground entitlement checks retain the mounted authorized screen while a still-valid grant is being refreshed.

| Mockup | Navigator entry | Create/replace/reuse |
|---|---|---|
| `Welcome` | Auth stack `Welcome` | Create `M/features/auth/WelcomeScreen.tsx`; split and then retire monolithic `LoginScreen.tsx` after preserving password/code/server selection and all auth tests. Use native Apple button only when available and configured; Google action follows the new target. |
| `Email-Code` | Auth `CodeRequest` → `EmailCode`; secondary `PasswordLogin` | Create code request/entry/password screens and reusable one-time-code input. Six digits, paste/autofill, server-derived resend/expiry state, change contact and error-under-field. Phone labels adapt to configured SMS availability. |
| `First-Run` | Home setup state inside the authorized tab shell | Create `features/onboarding/FirstRunChecklist.tsx`, rendered by Home. Derive progress from actual inverter binding, configured site and optional socket; no separate persisted onboarding route, arbitrary “complete” flag or restarted trial. |
| `Demo-Home` | `Home`, demo session | Refactor `features/demo/DemoApiClient.ts`/fixtures and demo banner; show Sample and disable all switching. Keep complete network isolation; Exit and Sign in leave the demo session. |
| `Main` | Tab `Home` | Replace `features/dashboard/DashboardScreen.tsx` presentation with hero/flow/export/devices/automations/activity cards. Keep focused resource and refresh lifecycle. |
| `Home-Night` | Same `Home` state | Theme-aware night hero using real astronomy/readings; next sunrise when available, truthful battery/import direction and completed-export wording. Night is independent of the user's chosen appearance. |
| `Energy` | Tab `Energy`, segment `Production` | Create `features/energy/EnergyScreen.tsx`; refactor existing generation logic into `features/generation/ProductionView.tsx`. Retire `GenerationScreen.tsx` as a separate tab after porting its period/date/data logic. |
| `Hourly-Table` | `ProductionHourlySheet` | Create `features/generation/HourlyTableSheet.tsx`; replace standalone `SolarEstimateDetailsScreen.tsx` once chart/detail/table behavior is ported. Selection shared with Production, mini range bars and attribution. |
| `Energy-Export` | `Energy`, segment `Export` | Create/refactor `features/sales/ExportView.tsx` from `SalesScreen.tsx`, `SalesDetailsScreen.tsx`; include Day/Month/Year/Custom, metric toggle, date/range pickers and hourly coverage sheet. Old sales detail route becomes an internal navigation adapter during conversion. |
| `Live-Readings` | Push `LiveReadings` from Home flow | Replace `dashboard/InverterDetailsScreen.tsx` with `features/readings/LiveReadingsScreen.tsx`; reuse `powerBalance.ts`, provenance/freshness formatters and source controls. Link `ReadingsLog`. |
| `Devices` | Tab `Devices` | Replace `features/devices/DevicesScreen.tsx` presentation, reuse `useSocketCommandActions.ts`, `SocketCommandActions.ts` and `SocketCommandCoordinator.ts`. Add control/source/pause chips and truthful state/power labels. |
| `Device-Sheet` | `DeviceSheet` | Create device control/detail/settings sheet plus timeline/support section; route to a full editor if content needs it. Create reusable `ManualOverrideSheet.tsx` for B7. |
| `Automations` | Tab `Automations`, segment `Rules` | Replace `features/rules/RulesScreen.tsx`; add sentence builder, status/evaluation summaries and template presets. Preserve immutable device identity and server version checks. |
| `Automation-Editor` | Push `AutomationEditor {id?:number}` | Refactor `features/rules/RuleEditorScreen.tsx`, `RuleDraftPolicy.ts`; range/stepper/source/schedule/advanced fields, evaluation box, navigation Save and delete confirmation. |
| `Activity` | `Automations`, segment `Activity` | Create `features/activity/ActivityView.tsx` and pure grouping/presentation helpers; split old `features/history/HistoryScreen.tsx` run view. Period/automation/changes filters, groups and Load older. |
| `Settings` | Push `Settings` from Home avatar | Replace `features/settings/SettingsScreen.tsx` with grouped navigation/plan/profile/preference rows. Remove More tab and MoreHome only when all destinations are reachable. |
| `Solar-Site` | Settings `SolarSite` | Create `features/settings/SolarSiteScreen.tsx` from site editor sections; location permission, two panels/compass, PV-DC confirmation, unsaved weather test and navigation Save. |
| `Tariff` | Settings `TariffExport` | Create `features/settings/TariffExportScreen.tsx`; port existing contract/date/zone/negative-price fields, use real contract values and calculated disclosures. |
| `Security` | Settings `SignInSecurity`, `PasswordSessions` | Create security/method/session screens; refactor `auth/AccountIdentityCard.tsx`, `AccountSecurityCard.tsx`. Preserve verified linking, fresh proof, export/deletion and original account identity. |
| `Connect` | Modal Connect stack `ConnectProvider {kind:inverter}` | Refactor `features/integrations/*` into provider/credentials/device-selection screens. List supplied provider descriptors, not the brands in sample HTML. |
| `Connect-Plugs` | Same `ConnectProvider {kind:socket}` | Same provider screen with plug selection and backend capability filtering. |
| `Connect-Shelly` | `ConnectCredentials {providerId,instanceId?}` | Reuse descriptor-driven `IntegrationFields`; provider help map, secret Paste, collapsed Advanced, OAuth continuation, inline test result and discovery count only when known. |
| `Connect-Choose` | `ConnectDevices {instanceId}` | Discovery/online/unsupported/unknown state rows, inline name override, quota from server, repeat selection idempotency. |
| `Paywall` | Modal `Paywall` and existing entitlement gate | Restyle `features/subscription/SubscriptionScreen.tsx`; reuse provider/controller/StoreKit bridge. Product prices and periods come from StoreKit; server remains access authority. Preserve restore, receipt retry/finish, expiry and resume behavior. |

Required mobile screens not drawn:

| Destination | Create or refactor |
|---|---|
| `ConnectedServices` | `features/integrations/ConnectedServicesScreen.tsx`, mirroring Web-Connected using real facts. |
| `DataRefresh` | `features/settings/DataRefreshScreen.tsx`, existing polling API and validation. |
| `Language`, `TimeZone`, `Appearance` | Picker screens/sheets under `features/settings/`; reuse language controller/caching, split site/settlement/display zones clearly, add theme override. |
| `ChangePassword`, `DeleteAccount`, `FreshProof`, `EditProfile` | Account stack screens/dialogs; proof-sensitive changes continue through authorized endpoints. |
| `Server` | Advanced settings/auth destination; keep saved URL, validation and account/session invalidation behavior. |
| `ReadingsLog` | `features/readings/ReadingsLogScreen.tsx`, split from History, raw/5-minute view, periods/gaps/pagination. |
| `ExportHourlySheet`, custom date picker, source/device picker | Shared picker/sheet patterns, accessible text/table alternatives and validated range. |
| `DesignGallery` | `dev/DesignGalleryScreen.tsx` only in development. |
| Every screen's skeleton/empty/error/offline/partial state | Shared UI states and fixtures; keep last safe data during background refetch. |

`M/core/components`, `core/theme.ts`, old chart presentation and old detail/More screens are transitional adapters, then removed after caller/test migration. Auth/session/language/command/subscription policy code is reused, not replaced merely to change styling.

## 3. Component inventory and gallery gate

Each web component has CSS isolation where applicable; Razor Pages use the same tokens and shared classes/partials or static component rendering. Mobile components consume `useTheme()` rather than importing fixed colors. Styling and data logic are separate.

| Vocabulary | Web inventory | Mobile inventory | Required states/behavior |
|---|---|---|---|
| Tokens/type/brand/icons | Appearance root, `UiText`, `BrandMark`, `Wordmark`, SVG icon component | Theme context, `ThemedText`, brand SVG, native tab symbols and existing Lucide icons elsewhere | Light/Dark/System, CJK fallback, tabular data figures, reduced motion. |
| Actions | `UiButton`, `IconButton`, `AvatarButton` | `Button`, `GlassIconButton`, avatar, Apple/Google auth actions | Primary/secondary/quiet/ghost/critical/sun, hover/focus/pressed/disabled/loading; inline spinner only. |
| Selection | `UiSwitch`, `SegmentedControl`, `FilterChip`, searchable `UiSelect`/listbox | Native themed Switch, segments/chips, select/picker sheet | Checked/unchecked/Sending, selected/current, disabled, clear focus and labels. |
| Numeric forms | `UiField`, `NumericField`, `Stepper`, `ThresholdRange`, time/date inputs | Field/unit input, stepper, accessible threshold range, time/date pickers | Field errors, min/max, off<on in new drafts, keyboard adjustment, units, unsaved drafts. |
| Surfaces/rows | `UiCard`, `StatTile`, `NavigationRow`, `DataRow`, `DeviceRow`, `StatusPill` | Card/stat/grouped lists/rows/pills | Data vs status colors, one hero value, state plus icon/text, null/unknown distinct from zero. |
| Data states | `Freshness`, `Banner`, `Skeleton`, `EmptyState`, `PartialDataNotice`, `CommandStatus` | Equivalent components plus global Demo banner | Loading, stale/offline with last time, error/recovery, unknown result, trial/sample, missing coverage. |
| Overlays/disclosure | Modal dialog, confirmation, disclosure, focus trap/return, drawer | Native-stack modal/sheet primitives, grabber/scrim, confirmation/picker/override sheets | Cancel, sending lock, dismiss/back, screen-reader isolation; full editor on pushed screen. |
| Charts | Production day/daily chart, Export chart, range bar, flow, battery gauge, device timeline | Same with `react-native-svg` | Shared readout/selection/table, current-hour outline, gaps, sunrise/sunset, nullable series. |
| Web layout | App shell/sidebar/mobile drawer, PageHeader, SettingsTabs, DataTable, TOC, sticky SaveBar | Screen, large title, glass nav actions, floating tab adapter | 1440/390, wide table scroll, sticky header/save, safe areas/keyboard and Dynamic Type. |
| Domain cards | Production/export/device/automation/evaluation/activity/integration/plan/account cards | Corresponding cards, activity groups, provider rows and checklist | Real counts/status/reasons, permissions, source/receipt timestamps. |

Geometry is pure: web `Components/Charts/ChartGeometry.cs` and mobile `ui/charts/geometry.ts` (scales, clipped Catmull-Rom→Bézier, bands, bars, hit-testing, range markers and timelines). Do not interpolate across unavailable intervals or smooth beyond valid segment endpoints. Domain formatters, rule-sentence composition, selection/date-window logic and activity grouping are separately unit tested.

Gallery fixtures cover all Brand-Components states, both themes, long translations, missing metrics, sparse chart series, command uncertainty and controlled accessibility states. Compare screenshots to the reference before adopting the library across screens. Production must not register `/_design` or mobile gallery navigation.

## 4. Verified gap matrix

Status refers to the current code, not the proposed implementation. Existing primitives may still require presentation work.

| Spec capability | Verified status | Source evidence and remaining work |
|---|---|---|
| Solar history Today/Week/Month | **Exists; B4/B5 partial** | `W/Services/SolarHistoryService.cs`, `SolarHistoryStore.cs`, `Api/MobileApiEndpointRouteBuilderExtensions.cs`: hourly means in every period, today's completed hours only. No observed energy, daily totals, future band, current-hour production, sunrise/sunset or CSV. |
| Export day/month/year/custom, credit, coverage/missing prices | **Exists; detail partial** | `W/Services/ExportSalesService.cs`, `ExportSalesContracts.cs`, `ExportSalesRange.cs`, `Api/ExportSalesApi.cs`: correct completed/current separation and decimal values. Need explicit missing-price intervals, detailed table/CSV presentation. |
| Power balance | **Exists** | `W/Shared/PowerBalance.cs`, `M/features/dashboard/powerBalance.ts`: quality, source alignment and freshness required; new DTOs must carry sufficient provenance. |
| Socket commands/lifecycle/receipts | **Exists** | `W/Integrations/IntegrationEndpoints.cs`, `SocketCommandLifecycle.cs`; `M/core/api/SocketCommandCoordinator.ts`. Actual route is `/api/v2/devices/{id:guid}/commands`, not the illustrative unversioned route. |
| Rule CRUD, enable/hysteresis/PV/window/cooldown/source | **Exists** | `W/Api/MobileApiEndpointRouteBuilderExtensions.cs`, `Data/RuleRepository.cs`, `Auth/AuthorizedRuleRepository.cs`; `src/DeyeSolar.Domain/Models/TriggerRule.cs`, `RuleConfigurationPolicy.cs`. One source inverter supplies both battery and PV. |
| Runs with SOC/solar/reasons | **Partial** | `W/Workers/RuleRunHistory.cs`, `RuleRunPresentation.cs`, `Data/HistoryQueryPolicy.cs`: repeated equal checks overwrite last timestamp/values; no count/start/ranges/RuleId. Retained seven days; not enough for the new feed. |
| Providers/fields/test/discovery/selection/OAuth | **Exists; metadata partial** | `W/Integrations/IntegrationEndpoints.cs`, `IntegrationSetupService.cs`, contracts/provider projects; mobile integration features. Test has no discovered count; online/model facts are not consistently available. |
| Trial socket quota | **Exists; usage/UI partial** | `W/Billing/TrialSocketQuota.cs`, `src/DeyeSolar.Domain/Billing/BillingAccount.cs`: limit exposed, usage missing. Counts the acting user's attributed bindings across installations. |
| Google/email/SMS/password/revoke-all/export/delete | **Exists** | `W/Auth/AccountIdentityEndpoints.cs`, `AccountIdentityService.cs`, `AccountSecurityEndpoints.cs`, `AccountSecurityService.cs`, deletion/export services. Availability and registration flags remain enforced. |
| Google unlink | **Missing** | No unlink/`RemoveLoginAsync` flow in current auth service/endpoints. The design's “existing link/unlink” overstates current behavior. |
| Language sync | **Exists** | `W/Localization/LanguageEndpoints.cs`, `UserLanguageService.cs`; mobile language context/controller. Bearer JSON endpoints and antiforgery-protected browser form are distinct. |
| Anonymous sign-in language selector | **Missing web handler** | `W/Pages/ChangeLanguage.cshtml.cs` requires authorization and redirects to `/settings`; add a validated anonymous preference-cookie handler on `/signin`, without a user claim. |
| Account ZIP export | **Partial; ZIP/settings/activity missing** | `W/Auth/AccountDataExporter.cs`, `AccountSecurityEndpoints.cs`: existing download is `solar-account.json`. Add the ZIP promised by Web-Account, including owned readings, automations, retained activity and settings; keep the released client's JSON operation. |
| B1 unified sign-in/register by code | **Partial** | Separate login/register challenge purposes and password-requiring registration API; internal `RegisterAsync` already accepts nullable password and creates private installation/trial. |
| B2 Apple identity/sign-in/link/revoke | **Missing** | `W/Billing/Apple*` is StoreKit billing, not Apple sign-in. Need separate identity options/verifier/flow/protected token storage. |
| B7 manual pause/resume | **Missing** | Existing command request and TriggerRule have no conflict policy/pause metadata. |
| B8 device timeline/on-duration | **Missing** | `W/Integrations/SocketObservationReader.cs`, `SocketInventoryReader.cs` do not persist socket observations. Command records alone cannot reconstruct physical state over 24 hours. |
| B9 evaluation snapshot | **Partial substrate; endpoint missing** | `src/DeyeSolar.RuleEngine/RuleEvaluator.cs`, `src/DeyeSolar.Domain/Models/RuleDecision.cs`: structured decisions exist in the worker, not durable/exposed snapshots. |
| B10 grouped activity/manual/state changes/summary | **Missing** | Legacy logs lack exact checks, actor/client and stable rule identity. Requires new durable activity/evaluation events plus command provenance. |
| B10 readings aggregation/gaps/CSV; B5/B6 CSV | **Missing** | `/api/readings` returns bounded raw array (1000 rows, 168h max). No aggregation/envelope/cursor/gap facts; reading DTO omits measured source details. |
| B13 sessions list/revoke one/others | **Partial substrate; API missing** | `W/Api/MobileSessionStore.cs`, `Auth/IAccountSessionStore.cs`, `Data/DeyeSolarDbContext.Sessions.cs`: persisted cookie and bearer sessions exist; no public ID, metadata/last-seen or list. |
| B13/B14 billing disclosures | **Partial** | Existing access includes trial end, subscription expiry, socket limit, serverNow and accessValidUntil. Product period/verified auto-renew details and quota usage absent. |
| B11 service facts | **Partial** | `W/Integrations/IntegrationDtos.cs` exposes configuration status; reads/receipts/weather/prices provide raw facts but no combined health presentation. Enabled is not equivalent to Connected. |
| Light/Dark/System | **Missing** | `W/App.razor` forces dark; `M/core/theme.ts`, `AppNavigator.tsx`, `mobile/app.json` use fixed dark presentation. No appearance override. |
| Templates | **Missing; client-only** | No preset editor flow. Use existing rule fields and honest descriptions; do not introduce different rule-engine semantics. |
| Geolocation | **Missing; client-only** | No browser/mobile location flow in current site form. Permission denial keeps manual entry. |
| Demo banner/no real network | **Partial** | `M/application/AppNavigator.tsx`, `features/demo/DemoApiClient.ts` isolate demo traffic but permit simulated switches. Redesign disables commands and changes copy/actions. |
| First-run checklist | **Missing; derived client state** | Existing new-account initialization creates an empty installation; derive setup status from actual settings/bindings. |
| Profile name/edit | **Missing additional gap** | Current `IdentityUser` has no display name/profile endpoint. Email linking does not replace an existing verified contact. |
| Single web settings save | **Partial substrate** | Site/polling/display endpoints are separate; add aggregate atomic save around existing settings writer rather than issuing several independent saves. |
| Settings primary-inverter selector | **Missing mutation in site form** | `W/Services/SiteSettingsService.cs` reads the saved integration selection and ignores submitted `SelectedDeviceSn`. `Integrations/IntegrationDeviceBindingWriter.cs` owns default binding changes. The aggregate save must explicitly coordinate that binding change with settings and version fences. |
| Yearly equivalent price/savings | **Missing bridge data** | Native BillingProduct has localized display text but lacks numeric price needed for truthful derived comparisons. Add StoreKit-derived helpers or hide unavailable comparisons. |

## 5. Additive API and persistence contracts

### 5.1 Common contract rules

- Installation reads: existing authenticated session, current membership, `InstallationPermission.Read`, own account billing access and scoped services/query filters. Blazor handlers also call `InteractiveSecurityContext.EnsureAsync`; middleware does not secure an already-open circuit by itself.
- Device switching uses `ControlDevices`; pausing automations also requires `ManageRules`. Settings mutations require `ManageSettings`; rule mutations retain `ManageRules` and existing configuration preconditions.
- Account reads/mutations are scoped to the current user. New account/auth routes must be included in billing, binding and permission exemptions, without weakening tenant routes. Cookie mutations validate antiforgery; mobile-only endpoints remain bearer-only. Existing identity/language JSON endpoints are bearer-only: Razor Page handlers invoke their services, rather than assuming browser cookies authorize those APIs. Auth callbacks use their validated OAuth state/correlation mechanism.
- Errors for new features use structured `code` plus values and field errors. Provider failure prose is not an authoritative reason for a historical gap. No credentials, bearer tokens, token hashes, raw Apple payloads or secrets in responses/logs.
- All timestamps identify measured/received/evaluated times and include UTC offsets; display zones differ from solar and settlement zones. W/kW and kWh remain explicit. Null never means zero.
- Existing request/response fixtures from build 11 are regression contracts. Existing options/status fields, trial start, appAccountToken, access deadlines, command behavior, routes and API 404 JSON behavior are preserved.

### 5.2 Authentication and account

| ID | Method and route | Request → response | Migration and authorization | Compatibility |
|---|---|---|---|---|
| A1 B1 | `POST /api/auth/code/start` | `{channel:email\|phone,destination}` → current challenge fields including verification ID/expiry/resend information | New one-time purpose `signin`, same delivery/normalization/limits; anonymous rate-limited endpoint. No schema change if current challenge storage suffices. | Old verification start/login/register routes untouched. Start response must not reveal whether account exists. |
| A2 B1 | `POST /api/auth/code/complete` | `{verificationId,code}` → existing `MobileAuthResponse` | Known confirmed identity signs in; unknown verified identity registers without password only under existing registration policy. Atomic account/private-installation/billing initialization; one-use challenge and concurrency tests. | Existing password sign-in, registration policy, email/phone collision policy and old account/trial meaning retained. `/signin` HTTP handlers use same service but issue cookie. |
| A3 B2 | `POST /api/auth/apple/exchange` | `{identityToken,rawNonce,authorizationCode,name?,email?}` → `MobileAuthResponse` | New Apple JWT/code exchange service, protected credential table and short-lived single-use flow state; anonymous rate-limited. Bundle audience for native, no trust in supplied email/name. | Separate `Auth:Apple` options; existing Google/password and StoreKit protocols unchanged. Optional Apple availability in `/api/auth/options`. |
| A4 B2 | `GET /auth/apple`; `POST /auth/apple/callback` (`form_post`) | Local return destination + operation → Apple authorization → validated callback → safe local redirect/cookie | Services ID audience, nonce/state/correlation/expiry/replay checks, fixed registered HTTPS callback; encrypted token storage. | Never turn generic auth redirects into open redirects; old `/signin-google` and `deyesolar://auth/callback` stay valid. |
| A5 B2/B13 | `POST /api/auth/apple/link/start`; `POST /api/auth/apple/link/complete` | Fresh proof → actor-bound flow; token/code/nonce plus flow ID → linked identity summary | Original authenticated bearer or CSRF-protected browser session, fresh proof, unique provider subject, protected Apple credentials; reject foreign identity ownership. | New linking path; existing Google linking request shape/PKCE unchanged. No automatic merge by matching email. |
| A6 B13 | `POST /api/account/identities/{provider}/unlink` | `{proof}` → updated method summary | Fresh proof, current user, last-usable-sign-in-method guard. Apple unlink includes durable revocation work. | Adds missing Google unlink. Never change verified contact replacement semantics implicitly. |
| A7 B2/B13 | `POST /api/account/proof/external/start` and `/complete` | `{provider}` → actor-bound reauthentication flow; provider proof → short-lived opaque proof ID | Extend `AccountFreshProofVerifier`/proof DTO with optional external proof. Bind to account/session/operation, short TTL, single-use. | Password/code proofs continue unchanged; necessary for Apple/Google-only accounts with no working contact delivery. |
| A8 B13 | `GET /api/account/profile`; `PATCH /api/account/profile` | Read `{displayName?,verifiedEmail?,verifiedPhone?}`; patch `{displayName}` → updated profile | Identity claim for display name, no migration required; authenticated account, CSRF on cookies. | Profile Edit can save the name independently; editing a verified email uses A15 and leaves the current address active until verification completes. |
| A9 B13 | `GET /api/account/preferences`; `PUT /api/account/preferences` | `{displayTimeZoneId?}` → selected effective zone | Optional per-user timezone claim with validated zone; current session/CSRF. No table needed. | Current `/api/settings/display` is installation-wide. Keep it unchanged; new personal preference falls back to it, rather than editing shared settings from Account. Language routes remain. Appearance synchronization is deferred as optional. |
| A10 B13 | `GET /api/account/sessions` | No body → `{sessions:[{id,platform?,client?,lastSeenAt?,createdAt,isCurrent}]}` | Add random public SessionId, nullable bounded metadata/lastSeen to current session table; throttle writes. Own-user read, no membership requirement. | Backfill random IDs only; unknown legacy metadata stays unknown. Never return token/hash. Existing login/logout/revoke-all remain. |
| A11 B13 | `POST /api/account/sessions/{id:guid}/revoke`; `POST /api/account/sessions/revoke-others` | `{proof}` → revoked result/204 | Fresh proof and own-user session IDs; preserve current session for revoke-others; invalidate cookie/bearer/circuit through existing validator. | Do not rotate the global stamp when only other sessions are revoked. Keep existing revoke-all behavior as its separate operation. |
| A12 B13/B14 | Extend `GET /api/billing/access` | Existing fields + optional `productId`, `planPeriod`, `autoRenewEnabled`, `renewalAt`, `trialDaysRemaining`, `socketUsage` | Add nullable verified renewal metadata on subscription storage; derive remaining days/usage from server clock and account-attributed bindings. Account authorization, accessible when expired. | No price returned as a substitute for StoreKit; no change to status, quota, trust deadline, trial/token/ownership policy. Show Renews only when verified; otherwise Expires/unknown. |
| A13 language | `POST /signin?handler=Language`; keep `POST /account/language` | `{language,returnUrl?}` → validated preference cookie and safe local redirect; authenticated form also saves the user preference | Anonymous antiforgery-protected sign-in form, allowlisted language and local destinations; no schema change. | Current signed-in endpoint remains, redirecting to `/settings/account`. Anonymous choice never mutates a user claim; mobile language API remains unchanged. |
| A14 B13 | `POST /api/account/export` | `{proof}` → `application/zip` download with manifest and owned account/installation datasets | Current-user fresh proof; bearer or CSRF-protected browser handler sharing the export service. Bounded streaming/archive generation; no table needed. Include new retained activity/settings/profile records, omit credentials/password hashes/token hashes and Apple revocation material. | New ZIP route; `/api/auth/security/export` keeps its JSON shape and filename for build 11. No data from another user's installation membership is exposed as owned export data. |
| A15 B13 | `POST /api/account/contacts/change/start`; `POST /api/account/contacts/change/complete` | `{channel,destination,proof}` → challenge; `{challengeId,code}` → updated verified profile | Fresh proof bound to actor/current contact version; destination normalization, uniqueness/collision checks, single-use delivery challenge and atomic Identity update. Recheck ownership/version at completion; preserve existing usable methods on failure. | New verified replacement workflow for Profile Edit; existing Add email/phone linking retains its meaning. No merge by email, no automatic replacement on external-provider sign-in and no trial/installation changes. |

Apple keys/options are independent from In-App Purchase signing keys and certificate verification. Configure Team ID, Key ID, key file, Services ID, bundle ID and public callback in `docs/accounts.env.example` using placeholders. Enable native entitlement in both app config and the committed Xcode project. Authorization-code exchange must obtain a revocable token, protected with durable keys, rather than merely storing an identity JWT. Deletion/unlink integrate revocation and recoverable pending work before discarding credentials; external failure must not silently lose revocation material or indefinitely prevent account deletion. Include new records in account export/deletion/offboarding recovery. See Apple's [TN3194](https://developer.apple.com/documentation/technotes/tn3194-handling-account-deletions-and-revoking-tokens-for-sign-in-with-apple) and [token revocation API](https://developer.apple.com/documentation/SigninwithAppleRESTAPI/Revoke-tokens).

### 5.3 Devices, automations, activity and readings

| ID | Method and route | Request → response | Migration and authorization | Compatibility |
|---|---|---|---|---|
| D1 B7 | Extend `POST /api/v2/devices/{id:guid}/commands` | Existing command ID/state + optional `onRuleConflict:pause\|once` → existing receipt + optional paused-rule IDs/conflict facts | Add nullable pause reason/at/actor/device/command fields to rules and actor/client/rule provenance to durable commands. Pause plus durable command admission in existing SQL transaction/locks; remote dispatch outside transaction. ControlDevices + ManageRules for pause. | Absent field exactly current once behavior. Existing command ID hashes remain valid; new choices are deduplicated/fenced. Uncertain result stays unresolved. |
| D2 B7/B9 | Existing rule create/update/enable routes | Existing payload → rule DTO + optional pause metadata; enable=true clears manual pause fields | Authorized repository and configuration-version concurrency fences, record state-change event atomically. | Resume from old clients also clears pause. Preserve accepted legacy equal thresholds; strict new editor validation is explicit and does not silently rewrite old rules. |
| D3 B8 | `GET /api/v2/devices/{id}/details` | Opaque inventory ID → current state/evidence, supported capabilities, provider/model?, source/circuit/name, controlling rules, last confirmation, addedAt? | Resolve ID against current scoped inventory/bindings; nullable AddedAt recorded for future selection only; no guessed old date/model. Installation Read. | New endpoint; `/api/devices` and `/api/devices/{id}/name` retain string IDs. Non-dynamic IDs have unavailable command/history capabilities where unsupported; never coerce an arbitrary ID into a GUID. Provider ID stays in support details, not list. |
| D4 B8 | `GET /api/v2/devices/{id:guid}/history?hours=24` | Bounded hours → `{start,end,intervals:[{from,to,state,evidence}],onSeconds,knownSeconds,partial}` | New tenant/device/generation-scoped state-observation events, measured/received times and confirmed receipt evidence; index by installation/device/time. Installation Read. | No old-history backfill from current cache; unknown intervals are neither on nor off. Record evidence without redefining provider acknowledgement as physical confirmation. |
| D5 B9 | `GET /api/rules/{id:int}/evaluation` | No body → `{configurationVersion,checkedAt,nextCheckAt?,conditions:[{kind,observed?,threshold?,status,reasonCode}],decision,state,freshness,source}` | New latest evaluation snapshot plus immutable check events from worker, reuse pure evaluator and actual source context. Installation Read. | GET never dispatches hardware. Separate last authoritative checked values from newer observations; no hypothetical result presented as an executed command. |
| D6 B10 | `GET /api/activity?from=&to=&ruleId=&changesOnly=&cursor=` | Validated ≤30-day range/filter/cursor → `{items,nextCursor,summary:{range,switches,onTime,confirmedCommands,partial}}` | New append-only evaluation and rule-state-change logs, stable RuleId/config/version, structured reasons/countable checks, command actor/source. 31-day retention and indexed bounded queries. Installation Read. | Dual-write while `/api/rule-runs` retains current shape/compression/query behavior. Existing compressed logs displayed only as legacy events without invented counts. |
| D7 B10 | `GET /api/activity/groups/{id}/checks?cursor=`; `GET /api/activity.csv` | Actor-scoped stable group/cursor or same feed filters → paged check details/CSV | Same activity store and snapshot boundaries, Installation Read. | Supports the drawn expandable groups and Web-Activity CSV action; no unbounded expansion. |
| D8 B10 | Extend `GET /api/readings` with `aggregate=5m` or `view=details`, plus cursor | Opt-in query → `{items, gaps, coverage, nextCursor, source}`; default remains current raw array | Add nullable future reading provenance/measurement timestamps where unavailable today; group metrics only from valid aligned same-source measurements; expose received vs measured times. Installation Read. | No parameter preserves max1000/order/max168h/array behavior. Detailed queries paginate rather than drop most of a seven-day interval. Generic gaps unless a real recorded failure supports a specific cause. |
| D9 B10 | `GET /api/readings.csv` | Same raw/aggregate/range query → UTF-8 CSV | Same service/authorization, bounded streaming/export limits, invariant machine-readable timestamps/numbers. | New route. Null cells stay blank, signs preserved; quote/escape values and neutralize spreadsheet formula injection in user text. |

Multiple enabled rules may target a device. Proposed B7 default: the pause choice explicitly lists and pauses all currently enabled controlling rules in the same admission transaction; once leaves all enabled. Re-read/version-check conflicts at admission so newly edited/added rules cannot escape the advertised choice. If the actor cannot ManageRules, show that pause is unavailable, retain Cancel/authorized once, and never bypass permissions. Command failure does not silently resume a paused rule; show both outcomes clearly.

Activity grouping is by stable rule/configuration/decision continuity, not display name alone. Groups cannot cross manual commands, edits, pause/resume, source changes or gaps. Cursor encodes a stable snapshot/time+ID boundary; grouping counts/ranges and weekly summary are independent of the current page. Summary on-time uses D4 evidence and coverage. Missing historical checks, durations or actors are disclosed, not reconstructed.

### 5.4 Production, export, integration facts and installation settings

| ID | Method and route | Request → response | Migration and authorization | Compatibility |
|---|---|---|---|---|
| E1 B4/B5 | `GET /api/solar/production?period=Today\|Week\|Month&date=` | Current period/date syntax → completed means, exact observed kWh/coveredSeconds/partial, daily buckets, best completed hour, current progress, full-day forecast bounds/central, sunrise/sunset/next-rise, source/model/retrieval times | New production presentation service using validated PV history; extend forecast acquisition for full local-day/astronomy. No required schema change apart from optional cache. Installation Read. | Existing `/api/solar/history` retains hourly mean-power/completed-hour semantics. Actual means ×1h is not used as energy because ≥90% coverage is not 100%. |
| E2 B5 | `GET /api/solar/production.csv` | Same period/date → chart/table twin in CSV | Same calculation/coverage service; Installation Read. | New route, actual/forecast gaps preserved. Include units and interval offsets; no scraping rendered labels. |
| E3 B6 | Extend `/api/sales` with optional detail fields/opt-in details | Existing query + optional detail → missingPriceHours, coverage/publication facts and completed hourly/daily rows | Reuse exact export/price calculations/storage. Installation Read. | Existing `through` remains valid; UI `to` maps to it (optional alias only with conflict validation). Current hour never enters totals. “Check prices again” preserves provider retry limits. |
| E4 B6 | `GET /api/sales.csv` | Same sales period/date/from/through query → rows/totals/coverage CSV | Same service/authorization; no new table. | Decimal precision retained until display/export formatting; no sum of rounded UI cells. |
| E5 B11 | `GET /api/v2/integrations/status`; optional provider kind/status facts | No body → service facts: scoped valid measured/received time, default inverter, socket count, last confirmation, forecast retrievedAt, price publication/missing intervals, structured status | Derive from existing durable/cached sources; persist minimal health evidence only where needed. Installation Read; administration fields remain admin-only. | Existing integration lifecycle DTO meanings stay. Never label enabled as Connected or imply prices are complete merely from maximum timestamp. |
| E6 B12 | `GET /api/settings/installation`; `PUT /api/settings/installation` | Read aggregate/available inverters/versions; save `{site,polling,display,primaryInverterId?,expectedVersion,expectedIntegrationVersions?}` → saved aggregate/versions or field/version error | New aggregate coordinator reuses scoped settings/binding policies in one transaction with integration locks/version fences; ManageSettings and CSRF; no separate table required. | Existing site/polling/display endpoints stay for old and split mobile screens, including read-only `SelectedDeviceSn`. Draft weather tests use unsaved values and never save credentials/switch devices. |

Production integration preserves device-time deduplication, real zero, invalid-source exclusion, maximum 10-minute gaps and no extrapolation. Return exact covered-segment energy and coverage instead of imputing uncovered minutes from a mean. Best hour uses comparable completed observed hours; future forecast is weather output with distinct provenance. Expected central output comes from the calculator, not the midpoint of bounds. Daily totals integrate local 23/25-hour days correctly. Missing/polar sunrise or sunset stays nullable. Open-Meteo attribution appears wherever forecast output is shown, including sheets/Home.

E6's inverter selector uses a selected enabled inverter binding ID, not the read-only legacy serial field. Save the default binding and editable settings atomically, validate PV-DC confirmation against the newly selected device, invalidate stale source snapshots and increment the affected configuration versions. Preserve rules with explicit source IDs; inherited default-source rules follow the existing source-resolution safety policy. A stale/foreign/disabled binding rejects the whole save. This operation never contacts hardware. Discard leaves both the saved binding and settings intact.

### 5.5 Migration sequence

Use several logical forward migrations with EF designer/snapshot updates and isolated SQL tests:

1. **Apple identity and session metadata:** protected Apple credentials/flow/revocation work; public session IDs and nullable client/last-seen fields. Profile/preferences can use Identity claims.
2. **Manual pause, command provenance and device state events:** nullable rule pause fields, nullable actor/source associations, optional binding AddedAt and new state-observation table.
3. **Evaluation/activity:** authoritative evaluation snapshots, append-only check/state-change records and tenant/rule/time indexes; retain legacy log table/API.
4. **Verified renewal/readings provenance:** nullable Apple renewal fields and any measured-source columns required for future reading detail; unknown old values remain null.

Every installation-private entity participates in existing `IInstallationOwned` query/write enforcement with scoped/composite foreign keys. Account tables use user ownership. Add bounded cleanup for new ephemeral flows/history, preserving command deduplication receipts/configuration/key retention. Do not assign historical observation quality, dates, actors, counts or names without evidence. Migrations must preserve all existing billing ownership, trial dates, appAccountTokens and the PV-quality repair. Production uses the privileged `--migrate-only` job before the restricted `validate` runtime; no automatic downgrade or second automation owner.

## 6. Web routes and permanent redirects

Create `W/Operations/LegacyUiRedirects.cs` for explicit server-side **301 GET/HEAD** redirects before tenant/billing gates and the Blazor fallback, with tests for casing, URL encoding and valid query migration. Handle Blazor-intercepted legacy navigation with compatible adapters or a forced server load so a running circuit receives the same canonical destination. Keep old API endpoints and intentional POST forms; do not blindly 301 or 307 credential-bearing legacy POSTs. Existing obsolete page GET templates can be removed after adapters are in place.

| Old URL | Destination | Query handling |
|---|---|---|
| `/generation`, `/solar-details` | `/energy` | Translate `Today/Week/Month` to `day/7d/30d`; preserve valid `date`. Drop unsafe returnTo and unknown navigation-only fields. |
| `/sales`, `/sales-details` | `/energy/export` | Translate period spelling; preserve valid `date` for Day/Month/Year. For Custom map `from` and legacy `through` to UI `from`/`to`; validate inclusive ≤366 days. |
| `/rules` | `/automations` | Preserve only supported non-sensitive filter state. |
| `/rules/edit` | `/automations/new` | No arbitrary external return URL. |
| `/rules/edit/{id:int}` | `/automations/{id}` | Keep integer rule ID. |
| `/runs` | `/activity` | Current filters are component state, with no legacy query contract. Accept only new supported validated filters; never infer a rule ID from name. |
| `/history`, `/inverter-details` | `/activity/readings` | Current filters are component state, with no legacy query contract. Accept only supported bounded new filters; default remains six hours. |
| `/account`, `/billing` | `/settings/account` | Preserve only supported local account section/operation state, not credentials or OAuth codes. |
| `/Login`, `/login`, `/register` | `/signin` | Safe local returnUrl mapping; password is still available. New registration occurs by verified code when policy allows. |
| `/verify` | `/signin?step=code` | Preserve valid existing challenge flow via server-side state/challenge ID adapter; do not put codes/passwords in the URL or strand already-sent codes. |
| `/`, `/devices`, `/settings` | Keep routes | Existing route is replaced in place; settings integrations block moves to `/settings/connections`. |
| `/privacy`, `/support`, `/Error`, `/Logout`, `/logout`, `POST /account/language` | Keep | Restyle public pages; retain mutation/authentication semantics and antiforgery. All OAuth callbacks, API routes, Blazor hubs, health and static asset routes also stay unchanged. |

New route parsing is strict and canonical. Invalid custom dates/ranges produce a field error and no widened query; missing optional periods use a documented default, while invalid supplied values are rejected. Preserve separate solar, settlement and display zones. Update the cookie LoginPath in `Operations/ApplicationServices.cs` to `/signin` and all links/email return paths. `/settings/account` stays outside tenant `InstallationGate`; adjust exact allowlists in `BillingAccessMiddleware`, `InstallationBindingMiddleware` and `InstallationPermissionMiddleware`. Expired users must still manage subscriptions, sessions, proof, export and deletion.

## 7. Implementation decisions

### Native iOS tabs first, with a measured fallback

Preferred implementation: `react-native-bottom-tabs` plus `@bottom-tabs/react-navigation` on iOS, behind a small tab adapter. Keep React Navigation/native-stack rather than migrating to Expo Router. Current Expo 57, RN 0.86.3, screens 4.26, committed native project and scene lifecycle make this plausible; they do not establish successful integration yet.

The [Callstack quick-start](https://oss.callstack.com/react-native-bottom-tabs/docs/getting-started/quick-start) supports an Expo config plugin/custom native build and excludes Expo Go. It does not certify this repository's exact stack. After approval, Phase 1 includes a disposable minimal integration/Release compile and simulator proof of four tabs, nested stacks, account isolation, background/foreground memory, light/dark/system, safe areas, accessible labels and the subscription gate. Record exact installed versions and screenshots before adoption.

Use native Liquid Glass when it satisfies those gates. If compile/lifecycle/accessibility or required visual customization fails, use the existing `@react-navigation/bottom-tabs` with a custom capsule and SDK-matched `expo-blur`, recording the failed criterion and deviation. Native system material/metrics may vary with iOS version; the fallback can match the 64px/16px/26px mockup geometry precisely. Android uses the shared custom presentation unless a native adapter is explicitly validated. No claim of compatibility based only on package availability.

2026-10-09 feedback decision: the native iOS bar did not satisfy equal spacing with translated labels, and the installed Callstack adapter exposes no item-width or item-positioning control. Use the shared `@react-navigation/bottom-tabs` glass capsule on iOS and Android with four equal flex columns, centered single-line labels and the existing 64px height / 16px side inset / safe bottom inset. Keep native stacks, scoped navigation memory, tab accessibility labels and test IDs. Retain the installed native tab dependencies without modifying package internals.

### MudBlazor: no retained controls planned

Current Mud use covers 37 component types, 376 opening tags in 22 Razor files: shell, forms, selects/autocompletes, switches, tables, alerts, dialogs/snackbars and icons. No disproportionate control was found. A searchable token-styled listbox replaces the three time-zone autocompletes; HTML time/date inputs and accessible range/numeric controls cover the editor. Port existing renderer/interaction tests instead of removing them. This project currently uses HtmlRenderer/custom renderer tests and Playwright, **not an installed bUnit package**; do not add bUnit solely because the prompt uses that label.

Remove `MudBlazor` 7.6.0, locked dependency references, `AddMudServices` in `Operations/ApplicationServices.cs`, `Localization/UiMudLocalizer.cs`, Mud-dependent wrappers in `UiTextConverters.cs`, `_Imports`, host CSS/JS and providers after the final consumer migrates. Preserve culture-aware numeric/time parsing, comma-decimal validation and field-error tests in independent helpers before deleting converter wrappers. Do not remove policy/services because a Mud wrapper currently invokes them.

### Fonts

Vendor licensed font files and license notices: Onest 400/500/600/700 and Unbounded 700, with Latin Extended/Cyrillic coverage. Web uses self-hosted woff2 `@font-face`, appropriate preload and system fallback; remove the current host's Google Roboto CDN reference. Mobile uses local files and `expo-font`; resolve actual family/weight names, preserve native asset/localization references, and display a controlled startup state/system fallback on load error. Never fetch fonts at runtime from a third party. CJK uses system fallback and is visually tested. Update Privacy's external-font disclosure and Support's old registration instructions to match the implemented behavior.

Prefer build-time embedding in the committed native project, with `expo-font` runtime readiness as needed; do not blindly regenerate iOS and erase the StoreKit bridge/scene setup. [Expo SDK 57 font documentation](https://docs.expo.dev/versions/v57.0.0/sdk/font/) supports plugin embedding and runtime loading. Static weights avoid relying on newer variable-font axis APIs. Keep Dynamic Type enabled and tabular figures for tables/axes.

### Token generation

Use a dependency-light Node script reading `docs/redesign/tokens.json`, validating token/theme parity and emitting deterministic CSS/TypeScript with generated-file notices. Add `--check` so CI rejects drift without writing files. Generate light root, explicit dark and `[data-theme="system"]` media variants; never let system dark override explicit Light.

Normalize existing CSS-valued shadow/blur/layout entries into typed native values, and explicitly alias missing same-meaning tokens such as light `switchOnKnob`; document derivations and contrast checks. Any missing semantic/component metric needed by the mockups is added to the source JSON, not hard-coded independently in two clients. Token tests check completeness/units/round-trip outputs, paired on-fill contrast and no geometry drift. Keep raster/vector asset generation separate from token generation.

### Appearance and brand

Initial choice is Light, as required by the settled prompt; System is an explicit option. Web reads a validated cookie before rendering `<html data-theme>` and uses media CSS only for System, preventing a flash. Mobile stores the override, resolves `useColorScheme`, uses automatic native interface style/status bars and a matching NavigationContainer theme. Account theme sync is optional and deferred; account display timezone is separated from shared installation settings.

Replace only user-visible DeyeSolar/Solar branding in titles, UI, mail/SMS authored by the app and permission text. Do not alter protocol/provider strings or identifiers merely containing “Solar”. Keep package/library/project/product IDs unchanged. Current app version/build label is read from real config rather than copied from Settings mockup.

### Truthful presentation and deliberate adaptations

- Preserve the earlier request to remove generic Refresh buttons everywhere: mobile uses pull-to-refresh with the scoped refresh registry; web retains automatic polling. Omitting Web-Dashboard's generic Refresh control is an explicit proposed deviation from that mockup. Check connection, Check prices again, command Check now, Restore purchases and explicit error recovery remain purpose-specific operations.
- B3's disabled commands override enabled sample switch attributes. A sample hero illustration on Welcome/sign-in must be labeled sample, never look like a real unauthenticated live reading.
- Device-sheet support/timeline and mobile custom export controls are required even when not drawn in their mobile mockup; use existing patterns rather than omit them.
- Match screen-specific button sizes/variants, including the Brand-Components 56px hero example and 52px screen CTAs. Visual chip/switch sizes may be below 44px, but their interactive hit area is at least 44px. Use 13px text minimum, 12px axes and 11px tabs where allowed; do not reproduce illegible non-axis 12px sample text.
- Dark surfaces/foregrounds come from tokens; ink-filled buttons need a contrasting paired foreground. Sunrise/sunset and expected-band end anchors use real astronomy, not fake zero weather samples. Keep all observed hours accessible in table/totals when the reference 05:00–20:00 chart window does not cover the site's daylight.
- Sunset alone does not prove “Home runs on battery”; show that sentence only when valid flow supports it. No production/offline value is converted into a night zero.
- “Renews” requires verified auto-renewal; “Changed 3 months ago” and session location are hidden unless actually known. No IP geolocation service is introduced.
- Yearly equivalent price and Save% require StoreKit decimal price/period data plus localized formatting. Add those bridge helpers additively or hide the comparisons; never parse displayPrice or copy dollars from Paywall.
- Existing rules may use equal/shared thresholds or different optional fields. Preserve untouched legacy rules, surface the compatibility state, and require a valid separated pair only when the new range is edited/newly created. The one combined source field retains existing battery/PV source semantics. “Daylight only” is a schedule preset with existing safety conditions, not a new clock-only rule engine.

## 8. Risks and mitigations

| Risk | Consequence | Plan/gate |
|---|---|---|
| API shape/meaning changes break released build | Login, control or entitlement regression | Build11 contract fixtures; omitted-parameter tests; default arrays/routes/units unchanged; backend additive release first. |
| UI restyle remounts authorized navigation | Last-screen loss and subscription snapshot returns | Preserve scoped navigation memory/controller; native switcher + resume tests on pushed screens, period/drafts, interrupted auth and expired/recovered grant. |
| Manual command and automation race | Device flips back or pause advertised incorrectly | Existing installation/integration locks/version fences; atomic pause+intent; dispatch outside DB; duplicate/rollback/concurrent worker tests. |
| New history gives invented durations/checks | Misleading activity/energy/timeline | New evidence storage from cutover, partial/unknown flags, exact energy integration, no reconstructed legacy counts. |
| New account route gated by billing/tenant | Expired customer cannot manage account/delete | HTTP Razor account page, exact exemption and circuit revocation tests, no widening of tenant permissions. |
| Apple auth config/revoke unavailable | Sign-in or account deletion incomplete | Separate Auth:Apple options, encrypted revocation recovery, unavailable-state UI, unit/API tests plus real configured device/web validation before release. Operator keys/callback setup remain an external prerequisite. |
| Native tabs/font/plugin project churn | App Store/native bridge build breaks | Early Release spike; preserve committed native scene/StoreKit/localization setup; minimal reviewed project diffs and existing native CI. |
| Clock/DST/coverage differences | Wrong totals, date navigation or trust deadline | Test 23/25h days, offsets, current hour exclusion, coverage and paired clock trust. Never use client date to grant access. |
| Activity volume/pagination races | Slow queries or duplicated/missing groups | Indexed tenant/time/rule events, bounded retention/pages, stable snapshot cursors, representative-volume query tests. |
| Broad source refactor loses security/tests | Stale circuit access or hidden regressions | Keep authorization in reused services; port assertions/fixtures, no skips/deletions/zero-test projects; phased small commits. |
| Localization and font expansion | Truncation/missing phrases in 15 languages | Translate during each phase, final completeness gate; Cyrillic/Latin Ext/CJK/long strings/Dynamic Type screenshots. |
| Screen-level simultaneous saves | Partial settings persisted | Aggregate atomic save/version contract and cancellation/discard tests; split mobile saves retain their scope. |

No production database, provider commands, account settings, purchase or external credential configuration is touched during planning/visual discovery. Later QA uses isolated synthetic/seeded data except explicitly arranged real Apple/provider release checks.

## 9. Order of work and phase gates

Every implementation phase ends in logical commits on this feature branch, green relevant tests and the prompt's quality gates. No push/PR until requested. Add English phrase keys and all real translations with each feature; Phase 5 is the completeness/accessibility sweep, not permission to leave earlier phases untranslated.

### Phase 1 — foundations

1. Token schema/generator/check mode and licensed font/brand assets.
2. Theme resolution/persistence/native configuration and shared primitives without replacing production screens yet.
3. Pure geometry/formatters, all component states and development galleries.
4. Native-tab compatibility spike and fallback decision, preserving existing navigator/subscription behavior.
5. Compare gallery against Brand-Components, both themes, focus/target sizes and representative languages. Release .NET/mobile/native compile and unchanged existing tests must pass.

### Phase 2 — additive backend

1. Freeze old-app fixtures/authorization contracts; migrations and new data producers first.
2. Unified code auth, anonymous language preference, profile/verified contact edit, ZIP export, preference/session metadata and Apple identity/proof/revoke.
3. Manual pause/admission, state observations and authoritative evaluation snapshots.
4. Activity grouping/expansion/summary/CSV, readings detail/aggregation/gaps/CSV.
5. Production energy/full-day forecast/astronomy/CSV; sales details/CSV; integration facts; atomic installation settings; verified billing disclosures.
6. SQL upgrade/fresh/repeat/tenant/deletion tests and released-client regression gate before any new UI depends on these endpoints. Do not present mocks as successful live Apple configuration.

### Phase 3 — web

1. Shared app/public shells, new routes/301 adapters and account/auth exemption changes.
2. Sign-in/account, then Home/Energy, Devices/Automations, Activity/Readings, Settings/Connections and undrawn public/confirm/error pages.
3. Port current HtmlRenderer/custom-renderer and Playwright tests; keep security and behavior assertions when changing markup selectors.
4. All 11 web mockups + undrawn states at 1440 and 390px, both themes, seeded actual app. Reference and implementation screenshots in the same browser, intentional differences recorded here.
5. Remove remaining Mud controls if none needed by transitional screens; package removal/lock audit follows last consumer.

### Phase 4 — mobile

1. Four-tab navigator, signed-out and Settings/modal stacks, retaining memory/pull/entitlement/command policies.
2. Welcome/code/password/first-run/demo, Home day/night, Energy production/export/sheets, Live readings/log.
3. Devices/history/override, Automations/editor/evaluation/activity, Settings/account/security/pickers, Connect and Paywall.
4. VoiceOver/Dynamic Type/Reduce Motion and offline/error/partial states. Native simulator screenshots of key flows at real sizes compared with all 24 mobile targets.
5. Typecheck, 298+ ported mobile regressions, iOS export/audit, full Release app/bridge build and StoreKit suite; foreground/switcher correctness is a required regression.

### Phase 5 — completion

1. All 15 phrase catalogs, native permission strings, source inventories and iOS localization sync.
2. Keyboard/focus/screen-reader/contrast/text/table alternatives in both themes and responsive/Dynamic Type checks.
3. Delete dead legacy screens/styles/adapters/providers and any remaining Mud references; update docs/README/routes/configuration.
4. Full all-project SQL/browser/provider/native gates, no skipped/zero tests, old-build against new-backend checks, then per-B1–B14/target/deviation/follow-up report.
5. Stop at the requested release scope; no deployment/push/PR inferred from this redesign handoff.

## 10. Test plan

| Area | Reuse/extend | New assertions |
|---|---|---|
| Auth/account | `AccountIdentityTests`, SQL identity/Google tests, `IdentityProofTests`, `PersistentSessionSecurityTests`, browser revocation tests; mobile auth/security/component tests | Known/unknown code identity, closed registration, collision/lockout/resend/replay/concurrent creation; Apple JWT key rotation/audience/nonce/issuer/algorithm/expiry/replay/relay/returning name; same-user external proof; link/unlink last method; revoke failure recovery; user-owned public session IDs/revoke-others/current circuit; anonymous language cookie/antiforgery/local redirects; verified contact replacement collision/concurrency; ZIP completeness, own-data scope, secret exclusion and unchanged legacy JSON export. |
| Commands/rules | `SocketCommandControlsTests`, command/integration SQL tests, `RuleRepositoryStateTests`, `SharedRuleConfigurationTests`, RuleEngine suite; mobile command/draft tests | Pause/once/absent field, both permissions, rollback/dedup/revision/worker races; unchanged old rules/equality/source/window/cooldown behavior; pause metadata cleared by old enable route; unknown command state/release. |
| Device/activity/evaluation | `RuleRunHistoryTests`, `RuleDecisionPresentationTests`, history/security retention tests | Truthful observation intervals/outages/generation changes, exact on-time coverage, immutable structured checks, grouping boundary/rename/delete/config change,30-day retention, stable paged expansion/summaries and read-only evaluation for every condition. |
| Production | Existing SolarHistory/quality/store/model/geometry tests and mobile energy chart tests | Exact observed energy vs covered mean, partial periods, current progress, daily bars, mixed/invalid sources, dedup, no gaps→zeros, local DST/polar astronomy, no future actual points, chart/readout/table/CSV identity. |
| Export | Existing sales calculator/range/store/service/statistics tests | All periods/custom≤366, signed negative-price policy, current-hour exclusion, explicit missing intervals, decimal row-total equality and CSV. |
| Readings/integrations/settings | Existing source validity/power balance/setup/OAuth/site tests | Per-metric averages/provenance/gaps, honest failure reasons, seven-day pagination, CSV escaping; descriptor provider kinds/count/unknown metadata, tests never switch/save; quota attribution and atomic settings/discard/version conflicts. |
| Billing/lifecycle/native | Existing billing SQL/deadline/browser tests, `SubscriptionController`/navigation mobile regressions, native StoreKit and app bridge suites | Optional billing fields don't change grants; expired/grace/canceled/stale/foreign statuses, exact server trial days, usage across installations; native tabs preserve last pushed screen/filters during foreground refresh and switcher snapshot. StoreKit localized prices and receipt retry before finish. |
| UI/a11y/i18n | Existing renderer and Playwright tests; mobile component fixtures, i18n and native sync scripts | Every target/required state, 1440/390 light/dark, visible keyboard focus/listbox/slider/dialog semantics, >=44 targets, chart text/table twins, VoiceOver/Dynamic Type/reduced motion, all15 real translations and no font CDN. |

Quality commands from the handoff remain required after implementation phases:

```sh
dotnet build DeyeSolar.sln --configuration Release
dotnet test DeyeSolar.sln --configuration Release
# Provision isolated SQL Server2022/SOLAR_TEST_SQL_CONNECTION and Chromium as CI does.
# Check TRX for every test project: >0 tests, all passed, no skipped tests.
python3 scripts/audit-nuget.py   # when NuGet dependencies change
node scripts/check-i18n.mjs --extract
node scripts/check-i18n.mjs
node scripts/sync-ios-localizations.mjs --check
cd mobile
npm run typecheck
npm test
npm run export:ios
npm run audit
```

Token generation `--check`, full iOS Release app/subscription bridge build, native StoreKit suite and screenshot/a11y gates supplement these commands. Discovery/extract mode writes inventories during implementation; Phase 0 did not modify product/localization inventories. Real Apple/provider checks need configured controlled accounts and cannot be certified by synthetic tests.

## 11. Phase 0 validation and approval boundary

Fresh baseline checks completed on unchanged product source:

| Check | Result |
|---|---|
| Release solution build | Passed, 0 errors; 268 existing warnings (including renderer BL0006 warnings). |
| RuleEngine suite | 22 passed, 0 failed/skipped. |
| Mobile typecheck | Passed. |
| Mobile tests | 298 passed, 0 failed/skipped/cancelled. |
| iOS JS export | Passed. |
| Mobile audit script | Passed under its existing policy; reports 16 affected paths and 2 dated upstream exceptions, absent from the iOS runtime source map. This is not a zero-advisory claim. |
| i18n catalog check | 15 catalogs, 1666 nonempty phrases with intact placeholders. |
| Native localization sync check | Passed for 15 languages. |
| Full solution SQL/browser/provider tests | **Not run in Phase 0:** no isolated `SOLAR_TEST_SQL_CONNECTION` configured. Running without it would skip SQL tests and would not satisfy the gate. Must provision CI-equivalent fixtures and run the complete suite before calling an implementation phase green. |
| NuGet audit/native archive/real Apple auth | No NuGet/native product dependency changed in planning; not rerun/certified here. Required when their relevant implementation changes land. |

Logs are outside the repository at `/tmp/smartsolar-phase0-*.log`; generated bundle output is ignored. The only new planning artifact is this file; the supplied handoff files remain intact. No product code has changed, and no work from Phase 1 has started.

**Stop here. Await the owner's explicit “go” before modifying product code.** Approval is for this implementation plan and its proposed defaults/deviations; no push, PR, deployment or App Store metadata update is authorized by that approval alone.


## 12. Approved implementation and release scope (2026-10-06)

The owner approved implementation of the complete redesign and explicitly requested commit, push to main and redeployment of mobile, web and the existing server. This supersedes the Phase 0 approval boundary and the handoff's default no-push rule. Work continues in the existing checkout on `feature/smartsolar-redesign`; no additional worktree, clone or copy is allowed.

Server operations follow the supplied transfer at `/Users/dmitryshapar/Downloads/Telegram Desktop/SolarManagement-server-transfer-20261006`, especially README-FIRST.txt, instructions/REDEPLOY-HANDOFF.txt and manifest.json. Secrets are read only locally and never printed. See `docs/production-redeploy.md` for the stable sanitized reference. Production cutover requires exact committed image, isolated migration rehearsal, fresh coordinated backup, separate privileged --migrate-only job, and a scoped Compose update of only deye-solar. Existing settings, keys, accounts, billing, data and neighboring services must be preserved.

Sign in with Apple is implemented and tested but live availability requires operator configuration and Apple Developer capability/provisioning. Read-only inspection found no server Auth Apple configuration. Availability remains false until those real prerequisites are supplied; no sample credentials or false enabled state will be introduced.

## 13. Implemented data adaptations and evidence

The visual references establish the design; their sample values do not establish production facts. Implemented screens use real scoped history, prices, observations, evaluations, account methods and provider capabilities. Missing facts stay unavailable, measured zero stays zero, source changes and missing intervals break curves, and partial observation coverage is disclosed. Raw signed market RCE is distinct from contract-adjusted money and deposit credit. Provider acknowledgement is distinct from an observed physical switch.

Installed signed descriptors supply connection forms and provider names. Apple/SMS/OAuth actions follow captured real availability. Account security, fresh proof, read-only permissions, server entitlement deadlines and legacy API compatibility remain enforced. General Refresh controls follow the owner's earlier removal request; explicit read-only weather/connection tests, price recheck and uncertain-command result recovery retain their separate purposes.

The connection manufacturer grid is descriptor-driven and uses provider initials when no approved brand asset exists. Discovery leaves live availability Unknown when the public contract omits it; switching support comes from canonical capability metadata. Optional device display names are validated and saved atomically with explicit selection, and actual account trial quota controls additional smart plugs. Unconfigured sign-in providers are hidden rather than displayed as working sample actions. These adaptations preserve the reference layout while showing only facts the installation can establish.

The required compass, bearing presets, editable numeric controls, draft weather check and save/discard guards are implemented; missing required controls are not accepted as design deviations. Unknown browser routes use public presentation without resolving unbound private installation services. Additional preserved polling/source/tariff/operator fields use the same tokens and accessible controls.

See `WEB_IMPLEMENTATION_REPORT.md` for the exact mockup-to-source route map, functional adaptations, browser evidence and targeted tests. See `IMPLEMENTATION_REPORT.md` for mobile coverage and final release receipts. Screenshot data is explicitly synthetic in an isolated QA installation; no real device commands, contact messages or external identity changes were used for visual checks.
