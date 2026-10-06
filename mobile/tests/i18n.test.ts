import assert from "node:assert/strict";
import { test } from "node:test";
import { currentLocale, languages, normalizeLanguage, setLocale, translate } from "../src/core/i18n";
import { formatWatts, formatTime, setDisplayTimeZone } from "../src/core/format";
import { ApiClient } from "../src/core/api/ApiClient";

test("every supported language translates settings and preserves interpolation", () => {
  try {
    assert.equal(languages.length, 15);
    for (const language of languages) {
      setLocale(language.code);
      const value = translate("Settings");
      assert.ok(value.length > 0);
      if (language.code !== "en") assert.notEqual(value, "Settings");
      assert.ok(translate("Request failed with HTTP {0}.", "503").includes("503"));
      assert.equal(translate("Owner's custom device"), "Owner's custom device");
    }
    setLocale("ru");
    assert.equal(translate("Settings"), "Настройки");
    assert.ok(!translate("Request failed with HTTP 503.").includes("Request failed"));
    const storedError = translate("Unable to load settings.");
    const storedHttpError = translate("Request failed with HTTP {0}.", "503");
    setLocale("pl");
    // Different English errors can share one Russian wording; either Polish equivalent is correct.
    assert.ok([translate("Unable to load settings."), translate("Settings could not be loaded.")].includes(translate(storedError)));
    assert.equal(translate(storedHttpError), translate("Request failed with HTTP {0}.", "503"));
  } finally { setLocale("en"); }
});

test("regional language preferences normalize and numerical display uses selected language", () => {
  try {
    assert.equal(normalizeLanguage("pt-BR"), "pt");
    assert.equal(normalizeLanguage("zh_Hans"), "zh");
    assert.equal(normalizeLanguage("invalid"), null);
    setLocale("pl");
    assert.equal(currentLocale(), "pl");
    assert.equal(formatWatts(1250), "1,3 kW");
    setDisplayTimeZone("UTC");
    assert.match(formatTime("2026-10-04T14:30:00Z"), /14:30:00/);
  } finally { setLocale("en"); setDisplayTimeZone(undefined); }
});

test("cached translated errors restore their arguments when switching back to English", () => {
  try {
    const customName = "Owner's custom device";
    setLocale("ru");
    const validation = translate("Enter a valid {0} for {1}.", translate("number"), "Region");
    const greeting = translate("Signed in as {0}", customName);
    setLocale("en");
    assert.equal(translate(validation), translate("Enter a valid {0} for {1}.", "number", "Region"));
    assert.equal(translate(greeting), translate("Signed in as {0}", customName));
    assert.equal(translate(customName), customName);
  } finally { setLocale("en"); }
});

test("rendered identity labels preserve custom names that resemble UI phrases", () => {
  try {
    for (const template of ["Signed in as {0}", "Configure {0}", "Use {0}", "Disable {0}", "Delete rule {0}", "Edit rule {0}",
      "Provider name: {0}. This changes the display name in Solar; device IDs and rules stay connected.", "Test {0}",
      "Link {0}", "Open {0}", "Switch {0}", "Switch {0} by hand?", "Pause {0}", "Edit automation {0}",
      "Provider name: {0}. Renaming changes only its display name; rules stay connected."]) {
      for (const name of ["Son of Earth", "Home"]) {
        setLocale("ru");
        const label = translate(template, name);
        assert.equal(translate(label), label, `${template} retains ${name} on rendering`);
        setLocale("en");
        assert.equal(translate(label), translate(template, name), `${template} retains ${name} after changing language`);
      }
    }
  } finally { setLocale("en"); }
});

test("simultaneously identical translated actions retain names without guessing a different action", () => {
  try {
    const name = "Simultaneous Son of Earth";
    setLocale("ru");
    const disable = translate("Disable {0}", name);
    const disconnect = translate("Disconnect {0}", name);
    assert.equal(disable, disconnect, "fixture exercises an actual catalog collision");
    assert.equal(translate(disable), disable);
    assert.equal(translate(disconnect), disconnect);
    setLocale("en");
    assert.equal(translate(disable), disable, "ambiguous cached wording is preserved instead of becoming Disconnect");
    assert.equal(translate(disconnect), disconnect, "ambiguous cached wording is preserved instead of becoming Disable");
    assert.equal(translate("Disable {0}", name), `Disable ${name}`);
    assert.equal(translate("Disconnect {0}", name), `Disconnect ${name}`);
  } finally { setLocale("en"); }
});

test("native requests include selected language before authentication and localize API errors", async () => {
  try {
    setLocale("pl");
    const client = new ApiClient({ baseUrl: "https://example.test", transport: async (_url, options) => {
      assert.equal(options.headers["Accept-Language"], "pl");
      assert.equal(options.headers.Authorization, undefined);
      return { status: 503, ok: false, text: async () => JSON.stringify({ message: "Unable to load settings." }) };
    } });
    await assert.rejects(client.request("/api/settings"), error => error instanceof Error && error.message === translate("Unable to load settings."));
  } finally { setLocale("en"); }
});
