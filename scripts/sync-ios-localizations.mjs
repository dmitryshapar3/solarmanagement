import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const languages = ['en', 'ru', 'uk', 'pl', 'de', 'fr', 'es', 'it', 'pt', 'nl', 'cs', 'tr', 'zh', 'ja', 'ko'];
const phrase = 'Allow DeyeSolar to use Face ID for secure sign-in.';
for (const language of languages) {
  const catalog = JSON.parse(fs.readFileSync(path.join(root, 'i18n', `${language}.json`), 'utf8'));
  if (!catalog[phrase]) throw new Error(`Missing native permission translation: ${language}`);
  const filename = path.join(root, 'mobile', 'ios', 'DeyeSolar', `${language}.lproj`, 'InfoPlist.strings');
  const content = `/* Generated from i18n/${language}.json by scripts/sync-ios-localizations.mjs. */\n` +
    `"NSFaceIDUsageDescription" = ${JSON.stringify(catalog[phrase])};\n`;
  if (process.argv.includes('--check')) {
    if (!fs.existsSync(filename) || fs.readFileSync(filename, 'utf8') !== content) throw new Error(`Outdated native localization: ${language}`);
  } else {
    fs.mkdirSync(path.dirname(filename), { recursive: true });
    fs.writeFileSync(filename, content);
  }
}
console.log('Verified native permission strings for 15 languages.');
