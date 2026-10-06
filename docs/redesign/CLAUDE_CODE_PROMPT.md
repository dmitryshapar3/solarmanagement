# Task: rebuild the SmartSolar web and mobile UI to the new design, and implement every feature the design introduces

You are working in the `solar-management` monorepo:

- Web: `src/DeyeSolar.Web` — Blazor Server on .NET 8 plus Razor Pages; MudBlazor today.
- Mobile: `mobile/` — Expo SDK 57, React Native 0.86, React Navigation 7, TypeScript.
- Backend and domain: `src/*`. Tests: `tests/*` and `mobile/tests`. Translations: `i18n/*.json` (15 languages).

The design handoff is in `docs/redesign/`:

- `DESIGN_SPEC.md` — read it **completely** first. It defines tokens, components, navigation, routes, the screen-to-code mapping, behaviours B1–B14 with acceptance criteria, and a gap matrix.
- `design/index.html` plus `design/*.dc.html` — static HTML mockups at real size (mobile 390 px, web 1440 px and fluid). Open each one in a browser (Playwright is available through the web test project). Read its inline styles for exact values and copy. `Before-*.dc.html` show the old app and are not targets. `Brand-Foundations` and `Brand-Components` are the design system and every component state.
- `tokens.json` — the single source for colors, type, spacing, radii and shadows (light and dark).
- `assets/smartsolar-mark.svg` — the brand mark.

## Decisions already made (do not reopen)

1. **Web UI**: build an in-house Razor component library on CSS custom properties generated from `tokens.json`, and **remove MudBlazor**. Keep a MudBlazor control only where rebuilding it is clearly disproportionate. If you keep one, restyle it fully and list it in the plan.
2. **Manual switching** of a device that an enabled automation controls asks the user: **"Pause <automation>"** (default) or **"Just this once"**, or Cancel. See spec B7.
3. **Appearance**: light by default; dark and "System" supported on both platforms. Today the UI is dark-only.
4. **Brand**: the user-visible name becomes **SmartSolar**. Identifiers (namespaces, assemblies, database objects, bundle ID, URL schemes, API routes, storage keys) stay unchanged.
5. **Navigation**:
   - Mobile: 4 tabs (Home, Energy, Devices, Automations); Settings opens from the avatar.
   - Web: sidebar with Home, Energy, Devices, Automations, Activity, Settings; new routes; every old URL becomes a permanent redirect (spec §5).

## Ground rules

- **Do not break the App Store build.** The released mobile app uses today's API. Backend changes must be additive: new endpoints, new optional fields, new tables through EF migrations. Never remove or repurpose existing fields, routes or meanings. If the server receives no new parameter, it behaves exactly as before.
- **Mockups contain sample data** (Kraków rooftop, Alex Nowak, 4.20 kW, PLN 52.06…). Never hard-code it. Bind real data. If real data cannot support an element (for example, a provider does not report a device model), hide that element. Never fake it.
- **Localization** follows `docs/localization.md`:
  - exact English phrase keys;
  - real translations in all 15 dictionaries (no English filler);
  - run `node scripts/check-i18n.mjs --extract` and then the check.
  - UI copy is the English text in the mockups.
- **Tests**:
  - CI fails on skipped tests and on test projects that run zero tests.
  - Port the existing bUnit, Playwright and mobile tests to the new UI instead of deleting or skipping them. Keep coverage equivalent or better.
  - Add tests for every new backend capability and for pure UI logic: formatters, chart geometry, activity grouping, rule-sentence builder, validation.
- **Security and tenancy**:
  - Every new endpoint is installation-scoped and authorized through the existing patterns (`InstallationPermissionPolicy`, `AuthorizedRuleRepository`, fresh proof for account-sensitive actions).
  - Never commit secrets. New configuration goes into options, with documented placeholders in `docs/*.env.example`.
- **Follow existing architecture and conventions.** Before changing an area, read the relevant docs: `docs/localization.md`, `solar-sales.md`, `solar-expected-power.md`, `accounts-deployment.md`, `app-store-backend.md`, `production-operations.md`.
- **Git workflow**:
  - Work on a new branch `feature/smartsolar-redesign` from `main`.
  - Make small logical commits, and keep build and tests green at the end of each phase.
  - Do not push or open a PR unless I ask.
- **When to ask**: if the design and the code disagree on behaviour that the spec does not settle, or a requirement is technically impossible, stop and ask with a concrete recommendation. Otherwise keep going.

## Phases

### Phase 0 — Discovery and plan (no product code changes)

Read the spec, every mockup, and the current code of every screen and endpoint listed in spec §7 and §11. Then write `docs/redesign/IMPLEMENTATION_PLAN.md` with:

1. Mapping: each mockup → files to create, replace or delete, and its route or navigator entry.
2. The component inventory for web and mobile, including the dev-only component gallery.
3. The **verified** gap matrix: confirm or correct spec §11 with file references, marking each item exists / partial / missing.
4. The API change list: method, route, request and response, migration, authorization, backward-compatibility note.
5. Decisions with reasons:
   - native iOS tab bar (`react-native-bottom-tabs`) vs a custom tab bar;
   - any MudBlazor control you keep;
   - font loading;
   - the token-generation script.
6. Risks, the test plan and the order of work.

**Stop and wait for my "go" after Phase 0.**

### Phase 1 — Foundations

- A token generator script (for example `scripts/generate-design-tokens.mjs`) produces:
  - `src/DeyeSolar.Web/wwwroot/css/tokens.css` — light, `[data-theme="dark"]`, and `prefers-color-scheme` for "System";
  - `mobile/src/ui/theme/tokens.ts`.
- Fonts:
  - web: Onest and Unbounded **self-hosted** as woff2;
  - mobile: `expo-font`;
  - Latin Extended and Cyrillic subsets; system fallback for CJK.
- Appearance switching on both platforms:
  - web: cookie plus `data-theme`, no flash on load;
  - mobile: `userInterfaceStyle: "automatic"`, a theme context, and the stored override.
- Brand: generate the app icon, favicon and apple-touch-icon from the mark; set the display name to SmartSolar.
- Component libraries implementing every state in `Brand-Components.dc.html`:
  - web: under `src/DeyeSolar.Web/Components/Ui`, with CSS isolation;
  - mobile: under `mobile/src/ui`.
  - Chart geometry lives in pure, unit-tested functions on both platforms.
  - Add a dev-only gallery: web `/_design` (Development environment only) and a mobile dev screen.
- Gate: compare the gallery with `Brand-Components.dc.html` using screenshots, and fix any differences.

### Phase 2 — Backend capabilities (additive)

Implement spec items:

- B1: unified sign-in or register by code;
- B2: Sign in with Apple — mobile token exchange, web form_post flow, linking, and Apple token revoke on account deletion;
- B7: pause the automation on manual switch, through an optional `onRuleConflict` on the command endpoint;
- B8: device 24-hour state history;
- B9: rule evaluation snapshot;
- B10: activity feed with grouped no-change checks, manual commands and rule state changes, plus a weekly summary; readings with 5-minute aggregation, gap detection and CSV; production and export CSV;
- B11: integration status facts;
- B13: session list and revoke-one; billing access with plan period, renewal date, trial days and socket quota.

Each item includes its migration, authorization, structured (localizable) reason codes, and unit plus API tests.

### Phase 3 — Web UI

- App shell, routes and redirects, and every web mockup (spec §7 web table) with all states:
  - loading skeletons, empty, error and offline;
  - partial data, unknown command result;
  - the < 1024 px layout with a drawer, and the dark theme.
- The sign-in page replaces Login, Register and Verify. Restyle the Privacy, Support, Error and 404 pages. Build the add-service flow as a dialog.
- Remove MudBlazor once nothing references it. Update the bUnit and Playwright tests.
- Visual QA:
  - run the app with seeded or demo data;
  - take Playwright screenshots of every page at 1440 and 390 px;
  - compare them side by side with the mockups opened in the same browser;
  - fix deviations, and list any intentional ones in the plan.

### Phase 4 — Mobile UI

- Restructure navigation: 4 tabs, Settings from the avatar, the More tab removed.
- Implement every mobile mockup (spec §7 mobile table) with all states:
  - sheets for device control, the hourly table and the manual-override choice;
  - the Connect flow (descriptor-driven fields, provider help steps, trial quota at device selection);
  - demo-mode banner;
  - Paywall with **StoreKit-localized prices** (never hard-coded);
  - the screens that were not drawn: Connected services list, pickers, change password, delete account, server, readings log.
- Dark theme, Dynamic Type, VoiceOver labels.
- Update the mobile tests.
- QA:
  - `npm run typecheck`, `npm test`, `npm run export:ios`;
  - if the iOS simulator is available, run `npx expo run:ios`, screenshot key screens with `xcrun simctl io booted screenshot`, and compare them with the mockups.

### Phase 5 — Localization, accessibility, cleanup

- All new phrases translated in 15 dictionaries; i18n check and iOS localization sync pass.
- Accessibility pass against spec §10: keyboard and focus on web, labels and roles on mobile, contrast in both themes.
- Remove dead code: old screens, MudBlazor, unused CSS and components.
- Update `README.md` and `docs/`: new routes, Apple sign-in configuration, appearance setting.
- Final report covering:
  - what was built, per spec item;
  - deviations from the mockups and the reasons;
  - follow-ups and open questions.

## Quality gates (run at the end of every phase)

```bash
dotnet build DeyeSolar.sln --configuration Release
dotnet test DeyeSolar.sln --configuration Release   # SQL-backed tests need SOLAR_TEST_SQL_CONNECTION (SQL Server 2022 in Docker, as in .github/workflows/tests.yml); if you can't run them, say so
python3 scripts/audit-nuget.py                      # when NuGet dependencies change
node scripts/check-i18n.mjs
node scripts/sync-ios-localizations.mjs --check
cd mobile && npm run typecheck && npm test && npm run export:ios && npm run audit
```

## Definition of done

- Every mockup in `docs/redesign/design/` (except `Before-*`) is implemented on its platform, in light and dark, with all component states.
- Behaviours B1–B14 work end to end with tests.
- Old web URLs redirect to the new routes.
- MudBlazor is removed (or only listed exceptions remain).
- All 15 languages are complete.
- All quality gates are green.
- The current App Store build keeps working against the new backend.
