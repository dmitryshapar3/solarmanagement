import en from "../../../i18n/en.json";
import ru from "../../../i18n/ru.json";
import uk from "../../../i18n/uk.json";
import pl from "../../../i18n/pl.json";
import de from "../../../i18n/de.json";
import fr from "../../../i18n/fr.json";
import es from "../../../i18n/es.json";
import it from "../../../i18n/it.json";
import pt from "../../../i18n/pt.json";
import nl from "../../../i18n/nl.json";
import cs from "../../../i18n/cs.json";
import tr from "../../../i18n/tr.json";
import zh from "../../../i18n/zh.json";
import ja from "../../../i18n/ja.json";
import ko from "../../../i18n/ko.json";

export const languages = [
  { code: "en", name: "English" }, { code: "ru", name: "Русский" }, { code: "uk", name: "Українська" },
  { code: "pl", name: "Polski" }, { code: "de", name: "Deutsch" }, { code: "fr", name: "Français" },
  { code: "es", name: "Español" }, { code: "it", name: "Italiano" }, { code: "pt", name: "Português" },
  { code: "nl", name: "Nederlands" }, { code: "cs", name: "Čeština" }, { code: "tr", name: "Türkçe" },
  { code: "zh", name: "中文" }, { code: "ja", name: "日本語" }, { code: "ko", name: "한국어" }
] as const;
export type Language = typeof languages[number]["code"];
const catalogs: Record<Language, Record<string, string>> = { en, ru, uk, pl, de, fr, es, it, pt, nl, cs, tr, zh, ja, ko };
// Error state can survive a language switch; recover its stable English key before translating it again.
const canonicalPhrases = new Map<string, string>();
for (const catalog of Object.values(catalogs)) {
  for (const [key, value] of Object.entries(catalog)) if (!canonicalPhrases.has(value)) canonicalPhrases.set(value, key);
}
export function normalizeLanguage(value?: string | null): Language | null {
  const code = value?.trim().toLowerCase().replace("_", "-").split("-")[0];
  return languages.some(language => language.code === code) ? code as Language : null;
}
let locale: Language = (() => {
  try { return normalizeLanguage(Intl.DateTimeFormat().resolvedOptions().locale) ?? "en"; }
  catch { return "en"; }
})();
export function currentLocale(): Language { return locale; }
export function formattingLocale(): string { return locale === "en" ? "en-GB" : locale; }
export function setLocale(value: string) { locale = normalizeLanguage(value) ?? "en"; }
// These arguments are account/device identities. Translating a rendered label again must retain their spelling.
const literalArgumentTemplates = new Set([
  "Signed in as {0}", "Configure {0}", "Use {0}", "Disable {0}", "Delete rule {0}", "Edit rule {0}",
  "Provider name: {0}. This changes the display name in Solar; device IDs and rules stay connected.",
  "Test {0}", "PV source: {0}", "{0}: selected source is unavailable. Its reference is preserved.",
  "Installed package version: {0}", "Version {0}",
  "Linked inverter for {0}", "Circuit type for {0}", "Save link for {0}", "Unavailable inverter · {0}",
  "Disconnect {0}", "Link {0}", "Open {0}", "Switch {0}", "Switch {0} by hand?", "Pause {0}", "Edit automation {0}",
  "Provider name: {0}. Renaming changes only its display name; rules stay connected."
]);
const literalArgumentSlots: Record<string, readonly number[]> = {
  "Switch {0} {1}?": [0],
  "When the battery reaches {0}%, turn on {1}. Turn it off below {2}%.": [1],
  "{0}: on {1}% · off {2}%": [0]
};
function literalArgument(key: string, slot: number) {
  return literalArgumentTemplates.has(key) || literalArgumentSlots[key]?.includes(slot);
}
// Two actions can have the same wording in a language. Remember formatted labels so
// a subsequent render does not guess a different action or translate a device name.
const formattedPhrases = new Map<string, { key: string | null; args: unknown[] }>();
const formattedPhraseLimit = 512;
function templatePattern(key: string) {
  const slots: number[] = [];
  const escaped = key.replace(/[.*+?^${}()|[\]\\]/g, "\\$&").replace(/\\\{(\d+)\\\}/g, (_, slot: string) => {
    slots.push(Number(slot));
    return "(.+?)";
  });
  return { key, slots, pattern: new RegExp(`^${escaped}$`) };
}
const templates = Object.keys(en).filter(key => /\{\d+\}/.test(key))
  .sort((a, b) => b.replace(/\{\d+\}/g, "").length - a.replace(/\{\d+\}/g, "").length)
  .map(templatePattern);
const reverseTemplates = Object.entries(catalogs).filter(([language]) => language !== "en").flatMap(([, catalog]) =>
  templates.filter(template => /[A-Za-z]{3}/.test(template.key.replace(/\{\d+\}/g, ""))
    && /\p{L}/u.test((catalog[template.key] ?? "").replace(/\{\d+\}/g, ""))
    && catalog[template.key] !== template.key)
    .map(template => ({ ...templatePattern(catalog[template.key] ?? template.key), source: template.key })))
  .sort((a, b) => b.key.replace(/\{\d+\}/g, "").length - a.key.replace(/\{\d+\}/g, "").length);
export function translate(phrase?: string | null, ...args: unknown[]): string {
  if (!phrase) return phrase ?? "";
  if (args.length === 0 && !Object.hasOwn(en, phrase)) {
    const formatted = formattedPhrases.get(phrase);
    if (formatted) {
      if (formatted.key === null) return phrase;
      const key = formatted.key;
      return translate(key, ...formatted.args.map((argument, slot) =>
        typeof argument === "string" && !literalArgument(key, slot) ? translate(argument) : argument));
    }
  }
  phrase = Object.hasOwn(en, phrase) ? phrase : canonicalPhrases.get(phrase) ?? phrase;
  let recoveredTemplate: string | undefined;
  if (!Object.hasOwn(en, phrase)) {
    // A bare translated string cannot identify which of two identically worded
    // actions created it. Keep its wording rather than inventing another action.
    const renderedPhrase = phrase;
    const matches = reverseTemplates.filter(template => template.pattern.test(renderedPhrase));
    if (new Set(matches.map(template => template.source)).size > 1) return phrase;
    for (const template of matches) {
      const match = template.pattern.exec(phrase);
      if (!match) continue;
      const values: Record<number, string> = {};
      template.slots.forEach((slot, index) => { values[slot] = match[index + 1] ?? ""; });
      phrase = template.source.replace(/\{(\d+)\}/g, (_, slot: string) => values[Number(slot)] ?? "");
      recoveredTemplate = template.source;
      break;
    }
  }
  let result = catalogs[locale][phrase];
  if (result === undefined && (locale !== "en" || recoveredTemplate !== undefined)) {
    if (recoveredTemplate === undefined && phrase.includes("; ")) return phrase.split("; ").map(part => translate(part)).join("; ");
    for (const template of templates) {
      if (recoveredTemplate !== undefined && template.key !== recoveredTemplate) continue;
      const match = template.pattern.exec(phrase);
      if (!match) continue;
      const values: Record<number, string> = {};
      template.slots.forEach((slot, index) => {
        const part = match[index + 1] ?? "";
        values[slot] = literalArgument(template.key, slot) || part === phrase ? part : translate(part);
      });
      result = (catalogs[locale][template.key] ?? template.key).replace(/\{(\d+)\}/g, (_, slot: string) => values[Number(slot)] ?? "");
      break;
    }
  }
  result ??= phrase;
  if (args.length === 0) return result;
  const formatted = result.replace(/\{(\d+)\}/g, (token, slot: string) => {
    const value = args[Number(slot)];
    if (value === undefined) return token;
    return typeof value === "number" ? value.toLocaleString(formattingLocale()) : String(value ?? "");
  });
  if (Object.hasOwn(en, phrase) && /\{\d+\}/.test(phrase)) {
    const previous = formattedPhrases.get(formatted);
    formattedPhrases.delete(formatted);
    formattedPhrases.set(formatted, previous && previous.key !== phrase ? { key: null, args: [] } : { key: phrase, args });
    if (formattedPhrases.size > formattedPhraseLimit) formattedPhrases.delete(formattedPhrases.keys().next().value!);
  }
  return formatted;
}
