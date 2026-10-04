import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
export const languages = ['en', 'ru', 'uk', 'pl', 'de', 'fr', 'es', 'it', 'pt', 'nl', 'cs', 'tr', 'zh', 'ja', 'ko'];
const catalogDir = path.join(root, 'i18n');

function walk(directory) {
  if (!fs.existsSync(directory)) return [];
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    if (['bin', 'obj', 'node_modules', 'dist', 'ios', 'android'].includes(entry.name)) return [];
    const filename = path.join(directory, entry.name);
    return entry.isDirectory() ? walk(filename) : [filename];
  });
}

// The application uses literal English phrases as stable catalog keys. Dynamic
// provider/domain messages are explicitly inventoried beside the catalogs.
export function collectPhrases() {
  const phrases = new Set();
  for (const filename of walk(catalogDir).filter(name => name.endsWith('-phrases.json'))) {
    const inventory = JSON.parse(fs.readFileSync(filename, 'utf8'));
    for (const phrase of Array.isArray(inventory) ? inventory : Object.keys(inventory)) {
      if (typeof phrase !== 'string' || !phrase.trim()) throw new Error(`Invalid phrase in ${filename}`);
      phrases.add(phrase);
    }
  }
  const files = [...walk(path.join(root, 'src')), ...walk(path.join(root, 'mobile', 'src'))]
    .filter(name => /\.(?:cs|razor|cshtml|tsx?|jsx?)$/.test(name));
  const patterns = [
    /\bT\s*\[\s*("(?:\\.|[^"\\])*")\s*\]/g,
    /\bT\s*\.\s*(?:Format|Translate)\s*\(\s*("(?:\\.|[^"\\])*")/g,
    /\bt\s*\(\s*("(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*')/g,
  ];
  for (const filename of files) {
    const source = fs.readFileSync(filename, 'utf8');
    for (const pattern of patterns) {
      for (const match of source.matchAll(pattern)) {
        const literal = match[1];
        // C# / TS literals have a shared common escape syntax. Single quoted
        // TS literals need an apostrophe escape removed before JSON decoding.
        const jsonLiteral = literal.startsWith("'")
          ? '"' + literal.slice(1, -1).replace(/\\'/g, "'").replace(/"/g, '\\"') + '"'
          : literal;
        try { phrases.add(JSON.parse(jsonLiteral)); }
        catch { throw new Error(`Unreadable translation literal in ${filename}: ${literal}`); }
      }
    }
  }
  return [...phrases].sort((a, b) => a.localeCompare(b, 'en'));
}

function placeholders(text) {
  return [...text.matchAll(/\{\d+(?:,[^}:]+)?(?::[^}]+)?\}/g)].map(match => match[0]).sort();
}

if (process.argv.includes('--extract')) {
  const phrases = collectPhrases();
  fs.mkdirSync(catalogDir, { recursive: true });
  fs.writeFileSync(path.join(catalogDir, 'en.json'), JSON.stringify(Object.fromEntries(phrases.map(phrase => [phrase, phrase])), null, 2) + '\n');
  console.log(`Extracted ${phrases.length} phrases.`);
} else {
  const phrases = collectPhrases();
  const failures = [];
  const canonical = new Set(phrases);
  // Proper names, SI units, mathematical expressions and words legitimately
  // shared with English were reviewed individually. New identity translations
  // require the same deliberate review instead of silently passing as filled.
  const unchanged = JSON.parse(fs.readFileSync(path.join(catalogDir, 'unchanged-allowlist.json'), 'utf8'));
  const symbolicPhrases = new Set(['of', '{0} of {1}', '{0}-{1} of {2}']);
  for (const language of languages) {
    const filename = path.join(catalogDir, `${language}.json`);
    if (!fs.existsSync(filename)) { failures.push(`${language}: missing catalog`); continue; }
    const catalog = JSON.parse(fs.readFileSync(filename, 'utf8'));
    const missing = phrases.filter(phrase => !(phrase in catalog));
    const extra = Object.keys(catalog).filter(phrase => !canonical.has(phrase));
    if (missing.length) failures.push(`${language}: ${missing.length} missing keys (${missing.slice(0, 8).join(' | ')})`);
    if (extra.length) failures.push(`${language}: ${extra.length} obsolete keys (${extra.slice(0, 8).join(' | ')})`);
    for (const phrase of phrases) {
      const translated = catalog[phrase];
      if (translated === undefined) continue;
      if (typeof translated !== 'string' || !translated.trim()) failures.push(`${language}: empty value for ${phrase}`);
      else if (JSON.stringify(placeholders(phrase)) !== JSON.stringify(placeholders(translated))) failures.push(`${language}: placeholder mismatch for ${phrase}`);
      if (language === 'en' && translated !== phrase) failures.push(`en: phrase is not its identity translation: ${phrase}`);
      if (language !== 'en' && translated === phrase && !unchanged[language]?.includes(phrase)) {
        failures.push(`${language}: unreviewed English identity translation: ${phrase}`);
      }
      if (typeof translated === 'string' && /ZXQPH\d+PHQXZ|§\s*\d+\s*§/.test(translated)) {
        failures.push(`${language}: translation bootstrap marker remains in ${phrase}`);
      }
      if (typeof translated === 'string' && !symbolicPhrases.has(phrase)
          && /[A-Za-z]{2,}/.test(phrase.replace(/\{[^}]+\}/g, ''))
          && !/\p{L}/u.test(translated.replace(/\{[^}]+\}/g, ''))) {
        failures.push(`${language}: translation dropped all meaningful words for ${phrase}`);
      }
    }
  }
  if (failures.length) {
    console.error(failures.join('\n'));
    process.exitCode = 1;
  } else console.log(`All ${languages.length} catalogs contain the same ${phrases.length} nonempty phrases with intact placeholders.`);
}
