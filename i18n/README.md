# Shared translations

The web app and mobile app use the same 15 static catalogs: English, Russian,
Ukrainian, Polish, German, French, Spanish, Italian, Portuguese, Dutch, Czech,
Turkish, Simplified Chinese, Japanese, and Korean. English phrases are stable
keys. No translation service is called by either application at runtime.

`web-phrases.json`, `mobile-phrases.json`, and `root-phrases.json` also inventory
dynamic UI text, provider descriptors, native prompts and server messages that
cannot be discovered from literal translation calls alone.

Run `node scripts/check-i18n.mjs --extract` to refresh the English union, then
translate new keys in every catalog. Run `node scripts/check-i18n.mjs` before
shipping. It checks source coverage, key parity, nonempty values, exact numeric
placeholder signatures, leftover bootstrap markers, dropped words, and
unreviewed English identity translations.

`unchanged-allowlist.json` records individually reviewed proper names, SI units,
mathematical expressions and legitimate words shared with English. Do not add
entries merely to silence an untranslated string.

Catalogs were bootstrapped from public static UI phrases with Google Translate,
then reviewed for electrical current and load, inverter power generation,
grid import/export direction, measurement history, monetary electricity value,
and settlement deposit credits. These credits are estimates, not loans, payouts
or current balances. Preserve this distinction when editing translations.
Preserve `{0}`, `{1}`, and other interpolation placeholders exactly, including
format specifiers. Translate complete sentences rather than fragments whenever
the application can provide a complete template.
