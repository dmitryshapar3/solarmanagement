import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const languages = ['en', 'ru', 'uk', 'pl', 'de', 'fr', 'es', 'it', 'pt', 'nl', 'cs', 'tr', 'zh', 'ja', 'ko'];
const permissions = {
  NSFaceIDUsageDescription: 'Allow SmartSolar to use Face ID for secure sign-in.',
  NSLocationWhenInUseUsageDescription: 'Use your location to set the solar site coordinates.',
};
for (const language of languages) {
  const catalog = JSON.parse(fs.readFileSync(path.join(root, 'i18n', `${language}.json`), 'utf8'));
  for (const phrase of Object.values(permissions)) {
    if (!catalog[phrase]) throw new Error(`Missing native permission translation: ${language}`);
  }
  const filename = path.join(root, 'mobile', 'ios', 'DeyeSolar', `${language}.lproj`, 'InfoPlist.strings');
  const content = `/* Generated from i18n/${language}.json by scripts/sync-ios-localizations.mjs. */\n` +
    Object.entries(permissions).map(([key, phrase]) => `"${key}" = ${JSON.stringify(catalog[phrase])};\n`).join('');
  if (process.argv.includes('--check')) {
    if (!fs.existsSync(filename) || fs.readFileSync(filename, 'utf8') !== content) throw new Error(`Outdated native localization: ${language}`);
  } else {
    fs.mkdirSync(path.dirname(filename), { recursive: true });
    fs.writeFileSync(filename, content);
  }
}
console.log('Verified native permission strings for 15 languages.');
