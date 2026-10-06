# Languages on web and mobile

Users can choose English, Russian, Ukrainian, Polish, German, French, Spanish, Italian, Portuguese, Dutch, Czech, Turkish, Chinese, Japanese or Korean in Settings. Both clients compile the same offline dictionaries from `i18n/*.json`. Provider setup instructions, chart captions, authentication errors, automation reasons, accessibility labels, public pages and native UI components use those dictionaries. Equipment names, user-entered names, brands, protocol values and measurement units retain their original values.

The account preference is stored as a `solar.language` claim in the existing Identity user-claims table. It belongs to the user, independently of shared installation settings; no database migration is needed for this preference. The web settings form posts to `/account/language` with an antiforgery token and reloads the page in the selected culture. Before signing in, the web chooses a saved language cookie or a supported browser language, respecting `Accept-Language` quality values.

The mobile client applies a selection immediately, stores it in AsyncStorage and sends `Accept-Language` on every API request. Account preferences are cached separately by server and username. Authenticated clients load and save the profile via bearer-only `GET` and `PUT /api/account/language`. Offline startup uses the local selection. When the updated mobile app connects to an older backend without that endpoint, local language selection still works; deploy the updated backend to enable account synchronization between web and mobile. A server or account change invalidates pending language operations.

Human-readable dates and numbers follow the selected language while respecting the existing installation time zone. API dates, SVG coordinates, device identities and calculation values retain invariant formatting.

Verification email subjects and bodies use the request language. SMS verification sends the corresponding Twilio Verify `Locale`, using `zh-CN` for Chinese; the provider controls its templates. See [Twilio Verify localization](https://www.twilio.com/docs/verify/supported-languages). Native iOS permission strings follow the system's app language and are generated from the same catalog.

## Updating translations

Use exact English phrase keys with numbered placeholders such as `Selected: {0}`. Keep dynamic server/provider phrases in the `*-phrases.json` inventories. After adding phrases, update every language dictionary; never fill missing translations with English solely to pass validation.

```bash
node scripts/check-i18n.mjs --extract
node scripts/check-i18n.mjs
node scripts/sync-ios-localizations.mjs
node scripts/sync-ios-localizations.mjs --check
cd mobile
npm run typecheck
npm test
npm run export:ios
```

The completeness check requires identical nonempty key sets in all 15 dictionaries and preserves every numbered placeholder. CI runs this check before the mobile build. The committed native iOS project includes all language resources; preserve these references when regenerating native files with Expo prebuild.
