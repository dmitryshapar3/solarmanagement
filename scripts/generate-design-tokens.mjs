#!/usr/bin/env node
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const source = JSON.parse(await readFile(resolve(root, 'docs/redesign/tokens.json'), 'utf8'));
const color = structuredClone(source.color);
// The original board names the light on knob switchKnob. Both themes expose the same key.
color.light.switchOnKnob ??= color.light.switchKnob;
const keys = Object.keys(color.light).sort();
if (keys.join() !== Object.keys(color.dark).sort().join()) throw new Error('Theme token keys differ');
for (const theme of Object.values(color)) for (const [key, value] of Object.entries(theme)) {
  if (!/^#[\da-f]{6}$|^rgba\([\d., ]+\)$/i.test(value)) throw new Error(`Invalid color ${key}`);
}
const kebab = value => value.replace(/[A-Z]/g, letter => '-' + letter.toLowerCase());
const declarations = theme => Object.entries(theme).map(([key, value]) => `  --${kebab(key)}: ${value};`).join('\n');
const css = `/* Generated from docs/redesign/tokens.json. Run node scripts/generate-design-tokens.mjs. */\n:root, [data-theme="light"] {\n  color-scheme: light;\n${declarations(color.light)}\n${Object.entries(source.radius).map(([key,value]) => `  --radius-${kebab(key)}: ${value}px;`).join('\n')}\n${source.space.map(value => `  --space-${value}: ${value}px;`).join('\n')}\n${Object.entries(source.type).flatMap(([key,value]) => [`  --type-${kebab(key)}-size: ${value.size}px;`, `  --type-${kebab(key)}-line: ${value.line}px;`, `  --type-${kebab(key)}-weight: ${value.weight};`, `  --type-${kebab(key)}-tracking: ${value.tracking ?? '0'};`]).join('\n')}\n${Object.entries(source.shadow).map(([key,value]) => `  --shadow-${kebab(key)}: ${value};`).join('\n')}\n  --font-ui: "${source.font.ui}", system-ui, sans-serif;\n  --font-display: "${source.font.display}", system-ui, sans-serif;\n  --web-sidebar: ${source.layout.webSidebar}px;\n  --web-content-max: ${source.layout.webContentMax}px;\n  --web-page-padding: ${source.layout.webPagePadding.split(' ').map(v => `${v}px`).join(' ')};\n  --min-touch-target: ${source.layout.minTouchTarget}px;\n  --chart-line-width: ${source.chart.lineWidth};\n}\n[data-theme="dark"] {\n  color-scheme: dark;\n${declarations(color.dark)}\n}\n@media (prefers-color-scheme: dark) {\n  [data-theme="system"] {\n    color-scheme: dark;\n${declarations(color.dark).split('\n').map(line => '  ' + line).join('\n')}\n  }\n}\n`;
const typography = Object.fromEntries(Object.entries(source.type).map(([key, value]) => [key, {
  fontSize: value.size, lineHeight: value.line, fontWeight: String(value.weight),
  ...(value.tracking ? { letterSpacing: Number.parseFloat(value.tracking) * value.size } : {}),
}]));
const nativeShadows = Object.fromEntries(Object.entries(source.shadow).map(([key, value]) => {
  const match = [...value.matchAll(/(-?[\d.]+)\s+(-?[\d.]+)px\s+([\d.]+)px\s+(rgba\([\d., ]+\))/g)].at(-1);
  return [key, match ? { shadowColor: match[4], shadowOffset: { width: Number(match[1]), height: Number(match[2]) }, shadowOpacity: 1, shadowRadius: Number(match[3]) / 2, elevation: Math.max(1, Number(match[3]) / 4) } : {}];
}));
const generated = { colors: color, typography, radius: source.radius, spacing: source.space,
  layout: { ...source.layout, mobileCardPadding: source.layout.mobileCardPadding.split("-").map(Number), webPagePadding: source.layout.webPagePadding.split(" ").map(Number) },
  font: source.font, shadows: nativeShadows, chart: { lineWidth: source.chart.lineWidth, barTopRadius: source.chart.barTopRadius }, blur: { glass: Number(source.blur.glass.match(/blur\(([\d.]+)px\)/)[1]) } };
const ts = `// Generated from docs/redesign/tokens.json. Do not edit.\nexport const designTokens = ${JSON.stringify(generated, null, 2)} as const;\nexport const colors = designTokens.colors;\nexport const typography = designTokens.typography;\nexport const radii = designTokens.radius;\nexport const spacing = designTokens.spacing;\nexport type ThemeColors = typeof colors.light | typeof colors.dark;\nexport type Appearance = 'light' | 'dark' | 'system';\n`;
let different = false;
for (const [path, content] of [['src/DeyeSolar.Web/wwwroot/css/tokens.css', css], ['mobile/src/ui/theme/tokens.ts', ts]]) {
  const target = resolve(root, path);
  if (process.argv.includes('--check')) {
    const existing = await readFile(target, 'utf8').catch(() => '');
    if (existing !== content) { process.stderr.write(`Generated tokens differ: ${path}\n`); different = true; }
  } else { await mkdir(dirname(target), { recursive: true }); await writeFile(target, content); }
}
if (different) process.exitCode = 1;
