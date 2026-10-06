# SmartSolar redesign — implementation spec

Source of truth for rebuilding the web (`src/DeyeSolar.Web`) and mobile (`mobile/`) UI.
Produced from the SmartSolar design canvas (October 2026).

- Mockups: `docs/redesign/design/` — open `design/index.html` in a browser.
- Tokens: `docs/redesign/tokens.json`
- Brand mark: `docs/redesign/assets/smartsolar-mark.svg`

---

## 0. How to read this package

1. Every `design/*.dc.html` file is a **static HTML mockup at real size**: mobile screens are 390 px wide, web pages are 1440 px wide and fluid. Open them directly in a browser. They reference `./support.js` (the design tool's runtime); it is not needed and its absence is harmless.
2. **Visual values come from the mockups**: colors, font sizes, weights, spacing, radii, shadows and the exact English copy are in their inline styles. **Behaviour comes from this spec.** If they conflict, the mockup wins on visuals and the spec wins on behaviour.
3. Mockups use **sample data** (Kraków rooftop, Alex Nowak, 4.20 kW, PLN 52.06…). Never hard-code it. Bind real data. Demo mode may reuse these values in its fixtures.
4. `Before-*.dc.html` are reconstructions of the **current (old)** mobile UI used for the audit. They are context, not targets.
5. `Brand-Foundations.dc.html` and `Brand-Components.dc.html` are the design system: type scale, color, spacing, and **every component state** (loading, disabled, sending, offline, unknown result, partial data, empty state…). Implement those states even where a screen mockup shows only the happy path.
6. Some screens and states were not drawn (listed in §7). Build them from the same tokens and components. Do not invent new visual patterns.

---

## 1. Brand and naming

- User-visible product name: **SmartSolar**. The wordmark is set in Unbounded 700. Replace user-visible "DeyeSolar" / "Solar" brand mentions in UI copy, page titles, the app display name, emails, SMS and iOS permission strings.
- **Do not rename identifiers**: .NET namespaces, assemblies, projects, database objects, the iOS bundle identifier, URL schemes (`deyesolar://…`), storage keys and API routes stay as they are. Brand is not identity.
- App Store metadata (`mobile/app-store/metadata.md`) is changed only when the owner asks.
- Mark: `assets/smartsolar-mark.svg`, a sun rising over the horizon. Generate from it: the iOS app icon (1024 px plus the sizes Expo needs), the web favicon (svg and png) and the apple-touch-icon.
- Voice (Brand-Foundations §06): say what it means for the home, not what the API returned.
  - "Latest reported inverter power · polled 13:42:18" becomes "Solar now · updated 12 s ago".
  - "SOC >= 75% | every 60s" becomes "When the battery reaches 75%, turn on Water heater".
  - "Completed-hour coverage: 13 of 13 observed · 13 valued." becomes "13 of 13 hours measured and priced".

---

## 2. Design tokens

Generate the platform files from `tokens.json`. Do not hand-copy values.

- Web: `wwwroot/css/tokens.css` with CSS custom properties for the light theme (`:root`) and the dark theme (`[data-theme="dark"]`, plus `prefers-color-scheme` when the user's choice is "System").
- Mobile: `src/ui/theme/tokens.ts` and a `useTheme()` hook.

| Token | Light | Dark | Use |
|---|---|---|---|
| bg | #F5F4F1 | #0D0C0A | page background |
| surface | #FFFFFF | #181715 | cards, rows, sheets |
| fill | #EAEAE6 | #232320 | segmented track, icon tiles, skeletons, grid lines |
| line | #DDDCD8 | #2E2E2B | input and outline-button rings, dividers |
| ink / ink2 / ink3 | #12120F / #51504C / #676662 | #F4F3F0 / #BEBEB9 / #999894 | text: primary / secondary / tertiary |
| sun | #FBCE25 | #FBCE25 | hero card, selected tab or nav item, the single key highlight; text on sun is always #12120F |
| sunTint | #FFF6D6 | derived | selected row, "running" status, explainer boxes |
| solar · battery · grid | #CB7F00 · #007549 · #3D66D0 | #C78200 · #14907A · #5F87E7 | **data only** (charts, flow nodes) |
| positive · warning · critical | #2A9754 · #DB640E · #CC3430 | #46B86E · #F68C36 · #EC5C52 | status, always icon + label |

Tints and text-on-tint pairs (`warningTint`/`warningText` and the others) are in `tokens.json`. Dark-theme status tints and texts are derived and contrast-checked (≥ 4.5:1). Only the night Home screen (`Home-Night.dc.html`) draws the dark theme. Use it as the reference for dark surfaces, the glass tab bar and switches.

Rules:

- **One sun per screen.** Yellow marks the single most important thing.
- Energy colors are for data only.
- Text is at least 13 px. 12 px is allowed only for chart axes and 11 px only for tab-bar labels.
- Tables and axes use tabular figures.
- **Fonts**: Onest (400/500/600/700) for all UI. Unbounded 700 only for the wordmark and the web sign-in headline. Latin Extended and Cyrillic subsets are required (15 UI languages). For zh, ja and ko, fall back to system fonts.
  - Web: **self-host** woff2 files. Do not load from the Google Fonts CDN (GDPR). The mockups use the CDN only for preview.
  - Mobile: load the font files with `expo-font`.
- Layout: mobile screen margin 16, top inset 58 (under the status bar), minimum touch target 44. Web: sidebar 248, content max-width 1160, page padding 32/24/48.

---

## 3. Theme (appearance)

- Light is the default. Dark follows the system. The user can override with Light / Dark / System (web: Settings › Account › Preferences; mobile: Settings › Appearance).
- Mobile: set `userInterfaceStyle: "automatic"` in `app.json`. Drive the status bar style from the active theme. Store the override in AsyncStorage.
- Web: set `data-theme` on `<html>` and persist the choice in a cookie, so the server renders the right theme without a flash. Syncing the choice to the account (a `solar.appearance` claim, like language) is optional.
- Charts, the flow diagram and the switches use theme tokens. No hard-coded hex values in components.

---

## 4. Components (shared vocabulary)

All states shown in `Brand-Components.dc.html` must exist. Interactive elements are at least 44 px.

| Component | Spec |
|---|---|
| Button | **primary**: ink fill, white text, one per screen. **secondary**: surface fill with a 1 px `line` inset ring. **quiet**: `bg` fill. **ghost/text**: transparent background. **critical**: criticalText on surface with a critical ring at 45 %. **sun**: only for the single most important action (for example the web save bar). Heights: 52 (mobile full-width CTA), 44 (default), 36 (compact). Radius is half the height. States: hover, pressed, disabled (40 % opacity), loading ("Saving…" with an inline spinner). |
| Glass icon button | 44 px circle on `glassButton` with the glass shadow. Mobile navigation (back, close, add). |
| Switch | 51×31. Light theme: ON = ink track with a white knob; OFF = #D9D8D3 track. Dark theme: ON = #F4F3F0 track with a #0D0C0A knob; OFF = #3A3A36 track. Has a **Sending** state (controls locked). Mobile uses the native `Switch` with track and thumb colors from tokens. Web uses `<button role="switch" aria-checked>`. |
| Segmented control | Switches views **inside** a section: Production / Export, Rules / Activity, Automation runs / Readings, Inverter / Smart plug. Track = fill, padding 3, item radius 9, selected item = surface with a soft shadow. When it changes the route, use links with `aria-current`; when it changes local state, use toggle buttons with `aria-pressed`. |
| Filter chips | Height 34–36, radius equal to half the height. Selected = ink fill with white text; unselected = surface with a line ring. Used for periods and filters. |
| Stepper | Numeric kW thresholds with ± buttons. |
| Threshold range | Dual-thumb slider (off and on levels with the hysteresis gap shown), a marker for the current battery %, and paired numeric inputs on web. Validation: off < on. |
| Pill / badge | Tones: neutral, sun, sunTint, ink (yellow text on ink), positive, warning, critical, grid, battery. Leading 8 px dot or 14 px icon. |
| Card | Flat, no border, surface fill. Radius 24 on mobile and 28 for large web cards. Padding 16–24. |
| Stat / KPI tile | Label, value, unit, sub-line. One hero number per screen. |
| Lists and rows | Navigation row (52 px: label, value, chevron), data row, device row (state + switch). Grouped lists use radius 20 with inset dividers. |
| Inputs | Label above (13/600, ink2). Field 44–52 px. Unit inside the field. Helper text below. **Errors appear under the field, never in a banner.** |
| Select | A button with a chevron. Opens a sheet or picker on mobile and a listbox popover on web. |
| Sheet (mobile) | Radius 38, grabber, scrim. Used for quick control (device), the hourly table and the manual-override choice. Editing happens on full screens. |
| Banners | info, warning (partial data), error ("Couldn't reach Deye Cloud" + Retry), trial, sample data. |
| Freshness | "Live · updated 12 s ago" / "Updated 6 min ago · pull to refresh" / "No connection · showing 09:10". |
| Device and command states | On or Off (confirmed by provider); Turning on… (controls locked); Offline (last seen 09:10); State unknown (waiting for a reading); **Result unknown** ("The plug didn't answer in time. It may have switched." + Check now / Allow a new command). Map these to the existing socket-command lifecycle (`SocketCommandLifecycle`, `SocketCommandCoordinator`). |
| Automation states | **Running**: holds its device on; yellow dot with an ink ring. **Waiting**: enabled but conditions not met, or outside the schedule. **Paused**: disabled by the user or by a manual override; white dot with a grey ring. Each state shows a reason line ("Paused since you switched Garden lights by hand"). |
| Empty state | No illustration: title, one sentence, primary action. Example: "No automations yet". |
| Loading | Skeleton blocks in `fill` shaped like the content. Spinners only inside buttons. |
| Web only | App shell (sidebar + content), PageHeader (h1 34/40, subtitle, actions), Settings tabs (underline style), DataTable (sticky header, scroll box for long or wide data, tabular numbers, selected row in sunTint with `aria-current`), sticky SaveBar (ink background, "Unsaved changes" summary, Discard and Save), disclosure (`<details>`), in-page table of contents. |
| Mobile only | Floating tab bar (§5), large titles, glass navigation buttons. |

---

## 5. Navigation and information architecture

### Mobile

- **4 tabs: Home · Energy · Devices · Automations.** Settings opens from the avatar on Home as a pushed stack. The "More" tab is removed:
  - Generation + Sales move to **Energy** (Production | Export).
  - Rules + run history move to **Automations** (Rules | Activity).
  - Readings move to **Live readings** (from the Home energy-flow card), which links to a readings log.
  - Account moves to **Settings**.
- Tab bar: iOS 26 floating capsule (glass, height 64, side insets 16, bottom 26). The selected tab is a sun pill.
  - Prefer the native iOS tab bar (Liquid Glass) through `react-native-bottom-tabs` + `@bottom-tabs/react-navigation`, if it works with Expo SDK 57 / RN 0.86 and the committed native iOS project.
  - Otherwise write a custom `tabBar` for `@react-navigation/bottom-tabs` that matches the mockup, using `expo-blur` for the glass.
  - Note the choice in the plan.
- Pushed screens: Automation editor, Live readings, Settings and its subsections, the Connect flow (modal stack), Paywall (modal).
- Sheets: Device, Hourly table, the manual-override choice (B7).
- Signed-out flow: Welcome → Email code → First run → Home. Demo: Welcome → Home in demo mode.

### Web

- Sidebar: Home, Energy, Devices, Automations, Activity | Settings, Help & support | user card (links to Settings › Account) + sign out.
- Responsive:
  - ≥ 1024 px: the sidebar.
  - < 1024 px: a top app bar with a drawer that holds the same navigation.
  - Content becomes a single column. Wide tables scroll inside their box. Every page must work at 390 px.
- Routes (keep the old URLs as permanent redirects):

| New route | Replaces |
|---|---|
| `/` Home | `/` |
| `/energy` (Production; `?period=day\|7d\|30d&date=`) | `/generation`, `/solar-details` |
| `/energy/export` (`?period=day\|month\|year\|custom&from=&to=`) | `/sales`, `/sales-details` |
| `/devices`, `/devices/{id}` | `/devices` |
| `/automations`, `/automations/new`, `/automations/{id}` | `/rules`, `/rules/edit`, `/rules/edit/{id}` |
| `/activity` (automation runs) | `/runs` |
| `/activity/readings` | `/history`, `/inverter-details` |
| `/settings` (Installation) | `/settings` |
| `/settings/connections` | the integrations block of `/settings` |
| `/settings/account` | `/account`, `/billing` |
| `/signin` | `/Login`, `/register`, `/verify` (update the cookie `LoginPath`) |
| `/privacy`, `/support`, `/Logout`, `POST /account/language` | keep; restyle the public pages |

---

## 6. Data visualization

- Palette:
  - actual production = solar-color line, 2.5 px;
  - expected range = band (`expectedBand`);
  - battery, grid and home use their energy colors.
  - The palette is CVD-safe and every series is also labeled.
- **Production (day)**:
  - x axis 05:00–20:00, y axis in kW.
  - The band is anchored to zero at sunrise and sunset; the actual line runs up to now.
  - "Now" = a dashed hairline with a label.
  - Selected hour = a soft column plus a ring marker.
  - **A readout above the plot is always visible**: selected hour, average kW, expected range, and a status pill (In range / Below / Above).
  - Selection works by tap, drag (scrub), hover and keyboard (←/→). The readout and the hour-by-hour table stay in sync.
- **Production (7 / 30 days)**: daily totals as bars against the expected range per day.
- **Export**:
  - Bars: hourly (day), daily (month or custom), monthly (year).
  - The current hour is in progress: draw it as an outlined bar (grid stroke, gridTint fill), excluded from totals.
  - A warning dot on days with missing prices, plus a banner.
  - kWh | PLN toggle.
- **Home hero mini chart**: band + actual line so far + now marker; sunrise and sunset labels below.
- **Energy flow diagram**: nodes Solar (sun fill), Grid (grid color), Battery (battery color), Home (ink). Line thickness and chevrons show direction and magnitude. Labels give kW and direction words (exporting / importing, charging / discharging).
- **Battery gauge**: battery-color fill plus markers for the on/off thresholds of the automation that uses the battery ("Solar surplus switches on at 75%").
- **Hour-by-hour row**: a mini range bar (band + solar dot; hollow dot for the hour in progress).
- **Device 24 h timeline**: ink = on, fill = off. Show a text summary ("On for 6 h 28 min") and a text alternative.
- **Gaps stay gaps.** Missing readings or prices are never drawn or summed as zero.
- Implementation:
  - Web: server-rendered SVG Razor components, no chart library.
  - Mobile: `react-native-svg`.
  - Keep scales, smoothing (Catmull-Rom to cubic Bézier, anchored at the ends so curves never loop) and bar geometry as **pure functions with unit tests** on both platforms.
  - Every chart has `role="img"` (web) or an accessibility label (mobile) with a text summary, and a table twin.

---

## 7. Screen inventory and mapping

### Mobile (`mobile/src`)

| Mockup | Screen | Replaces / extends | Notes |
|---|---|---|---|
| Welcome | Signed-out start | `features/auth/LoginScreen` | Buttons: Apple, Google, email. Link "Explore with sample data". Server choice hidden behind "Own SmartSolar server? Change server". Privacy link. |
| Email-Code | Code entry | LoginScreen (verification) | 6 digits; paste and autofill (`textContentType="oneTimeCode"`); resend timer; "Change email"; "Use a password instead". |
| First-Run | First sign-in checklist | new | Trial banner, 3 setup steps with progress (inverter, solar site, optional plug), "No readings yet" state. |
| Demo-Home | Home in demo mode | `features/demo` | B3 |
| Main | Home (day) | `features/dashboard/DashboardScreen` | B4 |
| Home-Night | Home at night, dark theme | DashboardScreen | Hero "The sun has set. Home runs on battery." with next sunrise; produced today vs expected; flow from battery; "All export hours are complete". |
| Energy | Energy › Production | `features/generation/*` | B5 |
| Hourly-Table | Sheet from Energy | `SolarEstimateDetailsScreen` | Hour-by-hour list with mini range bars; selected hour highlighted. |
| Energy-Export | Energy › Export | `features/sales/*` | B6 |
| Live-Readings | Pushed from the Home flow card | `InverterDetailsScreen`, `features/history` | Battery card with rule threshold marks, power right now with signs, balance check, readings log (last 6 h). |
| Devices | Devices tab | `features/devices/DevicesScreen` | B7, B8 |
| Device-Sheet | Device sheet | DevicesScreen controls | B7, B8 |
| Automations | Automations › Rules | `features/rules/RulesScreen` | B9 |
| Automation-Editor | Editor (push) | `RuleEditorScreen` | B9 |
| Activity | Automations › Activity | rule-run history | B10 |
| Settings | Settings (from avatar) | `features/settings/SettingsScreen` | Grouped list. The plan card links to App Store subscription management, or to the Paywall when on trial. |
| Solar-Site | Settings › Solar site | SettingsScreen site section | B12 |
| Tariff | Settings › Tariff & export | SettingsScreen sales section | B12 |
| Security | Settings › Sign-in & security | `AccountIdentityCard`, `AccountSecurityCard` | B13 |
| Connect, Connect-Plugs | Connect step 1 (Inverter / Smart plug) | `features/integrations/*` | B11 |
| Connect-Shelly | Connect step 2 (provider fields) | `IntegrationFields` | B11 |
| Connect-Choose | Connect step 3 (choose devices) | integration device selection | B11 |
| Paywall | Trial ended / upgrade | `features/subscription/SubscriptionScreen` | **Prices and periods come from StoreKit** (localized). Never hard-code "$29.99". Restore purchases, Terms and Privacy links. |

Not drawn but required, built from the system:

- Settings › **Connected services** list (mirror Web-Connected)
- Data refresh, Language, Time zone and Appearance pickers
- Change password
- Delete-account confirmation (fresh proof)
- Server (Advanced)
- Readings log
- Loading, empty, error and offline states for every screen

### Web (`src/DeyeSolar.Web`)

| Mockup | Route | Replaces | Notes |
|---|---|---|---|
| Web-SignIn | `/signin` | `Login`, `Register`, `Verify` (Razor Pages) | B1, B2. Split layout: brand panel + form. Language picker in the footer. |
| Web-Dashboard | `/` | `Pages/Index.razor` | B4. The "Manual override" block is removed; manual control lives on devices (B7). |
| Web-Energy | `/energy` | `Generation.razor`, `SolarDetails.razor` | B5 |
| Web-Energy-Export | `/energy/export` | `Sales.razor`, `SalesDetails.razor`, `Shared/SalesStatistics.razor` | B6 |
| Web-Devices | `/devices[/{id}]` | `Devices.razor` | List + detail panel. B7, B8 |
| Web-Automations | `/automations[/{id}\|/new]` | `Rules.razor`, `RuleEdit.razor` | List + editor side by side, templates. B9 |
| Web-Activity | `/activity` | `RunHistory.razor` | B10 |
| Web-Readings | `/activity/readings` | `History.razor`, `InverterDetails.razor` | B10 |
| Web-Settings | `/settings` | `Settings.razor`, `Shared/SiteSettingsEditor.razor` | Installation page with table of contents and a single save bar. B12 |
| Web-Connected | `/settings/connections` | `Integrations/IntegrationSettings.razor` | B11 |
| Web-Account | `/settings/account` | `Account.cshtml`, `Billing.cshtml`, `ChangeLanguage` | B13 |

Not drawn but required:

- The add-service flow in a dialog or drawer (the same 3 steps as mobile, B11)
- The verification-code step of sign-in (mirror Email-Code)
- Privacy and Support pages (restyle)
- Error page and 404
- Confirm dialogs: delete automation, delete account, unlink identity
- Change password
- Loading, empty and error states
- The < 1024 px layout

**MudBlazor is removed.** The web UI is built from an in-house Razor component set on the CSS tokens. Keep a MudBlazor control only where rebuilding it is disproportionate (for example a time-zone autocomplete). In that case restyle it fully to the tokens and list it in the plan. Remove the MudBlazor package once no longer referenced.

---

## 8. Behaviour (new or changed)

Each item has acceptance criteria (AC). "Backend" items are additive: **the API used by the current App Store build must keep working** (no removed fields, routes or changed meanings).

### B1 — Unified sign-in (web + mobile)

- One screen offers:
  - Continue with Apple
  - Continue with Google
  - email or phone + **Continue**, which sends a 6-digit code
- After the code is verified:
  - a known identity signs in;
  - an unknown email or phone **creates the account**, with the same rules as the current Register flow (trial starts).
- The password path stays available as the secondary "Sign in with a password instead" and still accepts username, email or phone.
- Web: `/signin` replaces `/Login`, `/register` and `/verify`. Keep every existing protection (antiforgery, rate limiting and lockout where present, generic error messages).
- AC: every current way of signing in still works; register-by-code yields the same account state as the old Register; tests cover new and existing users.

### B2 — Sign in with Apple (new)

- Mobile:
  - `expo-apple-authentication` (iOS only; hidden on Android).
  - Hash the nonce with SHA-256 (`expo-crypto`).
  - Send `identityToken`, `rawNonce`, `authorizationCode` and, on first sign-in only, name and email to `POST /api/auth/apple/exchange`.
- Web: Sign in with Apple through the Services ID with `response_mode=form_post` to `/auth/apple/callback`.
- Backend:
  - Validate the JWT against Apple's JWKS (cache the keys): `iss`, `aud` (bundle ID for native, Services ID for web), `exp`, nonce.
  - Map `sub` to an external login. Support private-relay emails.
  - Create the account on first sign-in, like Google.
  - Link and unlink in Account › Sign-in methods, with fresh proof, exactly like the existing Google linking.
- **Account deletion revokes Apple tokens** through Apple's REST revoke endpoint (App Store requirement for apps that offer Sign in with Apple).
- Configuration through options and environment variables: Team ID, Key ID, .p8 key, Services ID, bundle ID. Document them in `docs/accounts.env.example`. Never commit secrets.
- AC:
  - unit tests for token validation (bad `aud`, bad nonce, expired token, unknown `kid`);
  - linking tests mirror the Google linking tests;
  - deletion calls revoke (mocked in tests).

### B3 — Demo mode

- "Explore with sample data" opens Home with a banner: "You're exploring sample data · Numbers are made up. Switching is turned off." Actions: **Exit** (back to Welcome) and **Sign in**.
- The hero badge says "Sample" instead of "Live".
- Switches and commands render disabled and explain why.
- AC: no network calls to a real server in demo mode, as today (`DemoApiClient`).

### B4 — Home

- **Hero card** (links to Production):
  - "Solar now" + freshness;
  - status against the expected range (Within / Below / Above), with the range text;
  - today's mini chart with a now marker, sunrise and sunset;
  - Produced today, Expected today, Best hour (web shows all three stats).
- **Energy flow** diagram (§6) with battery % and the rule-threshold marker. Links to Live readings (mobile) or Readings (web).
- **Export today**: completed-hour kWh, estimated value, deposit credit (web), plus the current hour shown separately as "in progress, not in totals".
- **Devices**: rows with state and an inline switch that uses B7.
- **Automations**: status line per automation and the 2 most recent activity items.
- **Night state** (Home-Night): when production has ended, the hero shows "The sun has set. Home runs on battery." with the next sunrise. Export says "All export hours are complete".
- AC: each card links to its detail; the freshness and offline states from §4 are present.

### B5 — Energy › Production

- Periods: Day / 7 days / 30 days (the backend already supports `SolarHistoryPeriod.Today|Week|Month`). Date navigation goes back in time only, never into the future.
- KPI tiles: Produced so far, Expected today, Best hour, Right now (with status).
- Chart and readout behave as in §6.
- **Hour-by-hour table**: hour, actual average kW, expected range, mini range bar, status (In range / Below / Above / In progress / Upcoming).
- "Right now" card: inverter vs weather estimate.
- "How the forecast works" card, linking to Solar site settings.
- **Open-Meteo attribution "Weather data by Open-Meteo.com, CC BY 4.0" is mandatory** wherever forecasts appear.
- Web: Download CSV of the hourly table.

### B6 — Energy › Export

- Periods: Day / Month / Year / Custom. Custom has From and To (inclusive, at most 366 days) and uses the settlement time zone.
- KPIs: Exported (with credited after hourly netting), Energy value (estimated, hourly RCE), Deposit credit (estimated, value × 1.23, "not a cash payout"), Data coverage (hours measured / priced).
- **Missing-price banner** names the hours, says that missing prices are never counted as zero, and offers "Check prices again".
- Charts as in §6.
- Day-by-day table (web): sticky header, a totals row and a "partial" marker on days with missing prices.
- "How value is estimated" card: contract and start date, prices, netting, deposit multiplier, and the note about the OSD meter.
- Web: Download CSV.
- AC: totals equal the sum of the displayed rows (round only at display, consistently).

### B7 — Manual switching of a device controlled by an automation (decision: **ask**)

- If an **enabled** automation targets the device, switching it by hand opens a **choice** (mobile sheet, web dialog):
  - **"Pause <automation>"** — primary, default. Switches the device and pauses the automation until the user resumes it.
  - **"Just this once"** — switches the device; the automation may switch it back at its next check ("within 60 s").
  - **Cancel**.
- If no enabled automation targets the device, it switches immediately with no prompt.
- **Pause** = the automation becomes disabled with `pausedReason = ManualOverride`, `pausedAt`, `pausedByUserId` and the device. Turning the automation's switch back on (**Resume**) clears these fields.
- UI states:
  - the device chip reads "Manual control · rule paused";
  - the automation status reads "Paused since you switched Garden lights by hand";
  - Activity logs both "Turned off by hand" and "Automation paused".
- Backend:
  - extend the existing command endpoint additively, for example `POST /api/devices/{id}/commands` with an optional `onRuleConflict: "pause" | "once"`;
  - pause and command in one transaction, authorized per installation;
  - when the field is absent, the old behaviour stays ("once").
- AC: domain tests for pause/resume and the conflict detection; API tests for both options and for the absent-field default; the old app build keeps working.

### B8 — Devices

- **List**: state (On · 850 W / Off · since 22:40 / Offline / Unknown), provider, a chip for the controlling automation or "Manual control · rule paused", and a switch.
- **Detail** (web panel, mobile sheet):
  - big state and switch, power now, on since, last confirmed switch time;
  - **last-24-hours timeline** with "On for …";
  - "Controlled by" card linking to the automation;
  - the B7 hint text;
  - settings: name in SmartSolar (with "Restore <provider> name"), battery and solar source, circuit (single/three-phase);
  - **Details for support**: provider, model (if the provider reports one), device ID with copy, date added. The device ID appears only here, never in lists.
- Backend (new): `GET /api/devices/{id}/history?hours=24` returns state intervals and the on-duration, derived from confirmed command receipts and observed states. Add "added" and "model" only if they are available or cheap to persist; otherwise hide those fields. Never fake data.
- Command lifecycle states as in §4 (reuse the existing lifecycle).

### B9 — Automations

- **List**: cards that phrase the rule as a sentence ("When the battery reaches **75%** and solar averages **1.8 kW**, turn on **Water heater**. Turn it off at **55%**."), chips (schedule, check interval, cooldown), a status line (Running / Waiting / Paused + reason) and an enabled switch.
- **Editor fields**:
  - Name
  - Battery thresholds (threshold range + numeric inputs; off < on)
  - Require solar (≥ x kW, 1-hour average, skipped at ≥ 95% battery — the existing rule)
  - Target device
  - Schedule window (optional)
  - Advanced: check interval, wait between switches (cooldown), battery and solar source
  - Delete with confirmation
  - These map 1:1 to the existing rule fields; keep server-side validation as is.
- **"Right now" box** (new):
  - live evaluation: each condition with its observed value, threshold and pass/fail/skipped;
  - the resulting decision ("Right now: water heater is on");
  - "Checked 13:42 · next check in 18 s".
  - Backend: `GET /api/rules/{id}/evaluation`, built from the latest run outcome and the current readings.
- **Templates** (client-side presets that prefill the editor): Use solar surplus, Keep a battery reserve, Daylight only.
- Layout: web shows the list and editor side by side, with `/automations/{id}` deep links; mobile shows the list and pushes the editor.
- AC: an evaluation endpoint test for each condition kind; the editor's validation matches the server's.

### B10 — Activity and Readings

- **Automation runs** (mobile Activity; web `/activity`):
  - Consecutive **no-change checks are grouped** ("No change · 138 checks", time span, SOC and solar ranges, expandable).
  - Changes appear in bold with a human reason ("Battery reached 75% (76% now) and solar averaged 2.6 kW").
  - Also listed: **manual switches** (who, from where), **automation paused / resumed / edited**.
  - Filters: Changes / All checks, automation, period (24 h / 7 days / 30 days).
  - Web summary tiles: switches this week, device on-time, commands confirmed.
  - "Load older" pagination.
  - Backend (new): `GET /api/activity?from&to&ruleId&changesOnly&cursor` returns grouped items plus a summary. Compose it from rule runs, socket commands and a new rule-state-change log (an additive EF migration). Reasons are structured (code + values) and localized on the client.
- **Readings** (web `/activity/readings`; mobile readings log):
  - Latest snapshot tiles; "Measured … · received … · <provider>".
  - Balance check (reuse `PowerBalance`).
  - Periods 1 h / 6 h / 24 h / 7 days; every reading or 5-minute averages.
  - **Gap rows** ("No reading · Deye Cloud didn't respond in time. Gaps are never filled with zeros.").
  - Sign convention: minus means charging or exporting.
  - Backend: extend `GET /api/readings` additively with `aggregate=5m`, gap detection and a CSV variant.

### B11 — Connected services and the Connect flow

- **Service cards**: status (Connected / Working / Needs attention), key facts (inverter and its "default for automations" role, number of plugs, last reading time, last confirmed switch, forecast updated, prices published through <date> / missing hours), actions (Check connection, Find new plugs, Manage).
- Add-service grid by kind (Inverters: Deye, Huawei, Sungrow, Solis, Growatt; Smart plugs: Shelly, Sonoff/eWeLink, Tuya/Smart Life, Aqara, Netatmo). Use the provider list from the backend, not a hard-coded list. Brands are shown as letter monograms, not logos.
- "Advanced · provider packages" (versions, drafts, approved origins) stays collapsed and admin-only.
- **Connect flow** (mobile modal stack; web dialog):
  1. Brand (Inverter | Smart plug).
  2. Credentials:
     - fields rendered from the provider descriptor (existing `IntegrationFields`: text, secret, integer);
     - provider-specific "where to find it" steps from a small client map keyed by `providerId` (Shelly: "User settings → Authorization cloud key");
     - Paste on secret fields; Advanced fields collapsed;
     - OAuth providers show "Continue to <provider>" instead of fields;
     - **Check connection** shows the result inline ("Connection works · 4 devices found · nothing was switched").
  3. Choose devices:
     - discovered devices with online / offline / not switchable;
     - rename inline;
     - **trial quota**: "Your free month includes 1 smart plug". Extra plugs are marked Premium. The server enforces this (`TrialSocketQuota`).
- Backend: additive status facts (last reading time, last confirmed command, price coverage date and missing hours, forecast fetch time) on the integrations and settings endpoints.

### B12 — Installation settings (web `/settings`; mobile Solar site + Tariff + Data refresh)

- Sections:
  - **Location**: name, latitude, longitude, solar time zone; "Use this browser's / my current location" (web geolocation; mobile `expo-location` with a localized permission string); "Test weather here" (existing test, uses unsaved values).
  - **Panels**: up to 2 groups with capacity (kWp), tilt (°), facing (° with a compass dial and N/E/S/W), total kWp, "capacity 0 = unused".
  - **Inverter reading**: inverter + the PV-DC confirmation checkbox.
  - **Tariff & export**: contract, contract start, settlement time zone, negative-price switch.
  - **Data refresh**: polling interval.
- Web: one form with a single sticky **save bar** ("Unsaved changes · <section>" + Discard + Save changes) and a table of contents. Mobile: Save in the navigation bar per screen.
- Validation messages appear under fields (ranges: latitude −90…90, longitude −180…180, tilt 0…90, bearing 0…359).

### B13 — Account (web `/settings/account`; mobile Settings + Security)

- **Plan card**: Premium (monthly/yearly, renews <date>, "Billed by the App Store" + Manage), or Trial (N days left, 1 plug, upgrade path in the iOS app), or Ended. Backend: extend `GET /api/billing/access` additively with plan period, renewal/expiry date, trial days left and socket quota/usage.
- **Profile**: name, email, Edit.
- **Preferences**: Language (existing), Time zone (existing display time zone), Appearance (§3).
- **Sign-in methods**: Apple (B2), Google (existing link/unlink), Email (verified), Phone for SMS codes, Password (change).
- **Sessions** (new):
  - list devices and browsers with platform, client and last-seen time (location only if already available — no new third-party IP lookups);
  - sign out one session; sign out all other devices.
  - Backend: `GET /api/account/sessions`, `POST /api/account/sessions/{id}/revoke`, `POST /api/account/sessions/revoke-others`, extending `IAccountSessionStore` (additive migration).
- **Your data**: Download my data (existing export), Delete account (existing, plus B2 revoke), both behind fresh proof (`AccountFreshProofVerifier`).

### B14 — Trial and Premium presentation

- Web sidebar user card subtitle: "Premium · yearly" or "Trial · N days left".
- Mobile Settings plan row: Premium links to App Store subscription management; trial or ended opens the Paywall.

---

## 9. Copy and localization

- The English strings in the mockups are the source phrases. Follow `docs/localization.md`:
  - exact English phrase keys with numbered placeholders;
  - add every new phrase to **all 15** dictionaries in `i18n/` with real translations (never English filler);
  - run `node scripts/check-i18n.mjs --extract`, then `node scripts/check-i18n.mjs`, then the iOS sync script;
  - dynamic server and provider phrases go in the `*-phrases.json` inventories.
- Units and brands are not translated (kW, kWh, PLN, RCE, SmartSolar, provider names).
- Dates and numbers follow the selected language. Times use the installation time zone (settlement time zone for export).
- Use sentence case. Use "·" as the inline separator. Use the "−" minus sign for negative power. No exclamation marks, no jargon (SOC → "battery charge").

---

## 10. Accessibility

- WCAG 2.2 AA: the palette is pre-validated; keep text at ≥ 4.5:1 (≥ 3:1 for 24 px+).
- Touch targets ≥ 44 px.
- Web: visible `:focus-visible` (3 px outline in `focusRing`, offset 2); real `<button>`, `<a>`, `<input>` + `<label>`; `aria-current` for the current nav item, tab and selected row; `role="switch"` + `aria-checked`; `aria-live` on readouts and command status.
- Mobile: accessibility roles and labels on every control; Dynamic Type supported (cap the scaling only where layouts break); Reduce Motion respected.
- Charts: a text alternative plus a table twin. Never rely on color alone.

---

## 11. Gap matrix (verify against the code — this is a starting point)

| Capability in the design | Status | Where |
|---|---|---|
| Solar history day / week / month | exists | `SolarHistoryService` (`SolarHistoryPeriod`) |
| Export sales day/month/year/custom, credited, coverage, missing prices | exists | `ExportSalesService`, `/api/sales` |
| Power balance check | exists | `Shared/PowerBalance.cs`, mobile `powerBalance.ts` |
| Socket command lifecycle and receipts | exists | `SocketCommandLifecycle`, `/devices/{id}/commands` |
| Rules CRUD, enable toggle, hysteresis, PV average, window, cooldown, source | exists | `/rules`, `RuleEdit.razor` |
| Rule runs with SOC / solar values and reasons | exists | `/rule-runs`, `RuleRunPresentation` |
| Integrations: providers, fields, test, discovery, device selection, OAuth | exists | `IntegrationManagementEndpoints` |
| Trial socket quota | exists (server) | `TrialSocketQuota` — expose it to the UI |
| Google sign-in and linking, email/SMS codes, password, revoke all, export, delete | exists | `Auth/*` |
| Language preference sync | exists | `/api/account/language` |
| **Sign in with Apple** (mobile + web + revoke on delete) | missing | B2 |
| **Unified sign-in-or-register by code** | partial | B1 |
| **Manual override → pause automation** | missing | B7 |
| **Rule evaluation snapshot ("Right now")** | missing | B9 |
| **Activity feed** (grouped runs + manual + rule state changes + summary) | missing | B10 |
| **Device 24 h state history** | missing | B8 |
| **Readings 5-minute aggregation, gaps, CSV**; export and production CSV | missing | B5, B6, B10 |
| **Sessions list and revoke one** | missing | B13 |
| **Plan period / renewal / trial days / quota in billing access** | partial | B13 |
| **Integration status facts** (last reading, last confirmed command, price coverage) | partial | B11 |
| **Light / dark / system appearance** | missing (dark-only today) | §3 |
| Templates, geolocation, demo banner | client-only | B3, B9, B12 |
