import { execFile } from "node:child_process";
import { readdir, readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";

const mobile = fileURLToPath(new URL("../mobile/", import.meta.url));
const run = promisify(execFile);
let stdout;
try { ({ stdout } = await run("npm", ["audit", "--json"], { cwd: mobile, maxBuffer: 10 * 1024 * 1024 })); }
catch (error) {
  if (error.code !== 1 || !error.stdout) throw error;
  stdout = error.stdout;
}
const audit = JSON.parse(stdout);
if (audit.error || !audit.vulnerabilities || !audit.metadata) throw new Error("npm advisory audit did not complete.");

// Two upstream build-tool advisories have no fixed releases on 2026-10-05.
// A dated exception is valid only for these exact versions, advisories and absent runtime modules.
const exceptions = new Map([
  ["braces", { version: "3.0.3", advisory: "https://github.com/advisories/GHSA-vfj7-8cjw-p6xm" }],
  ["node-forge", { version: "1.4.0", advisory: "https://github.com/advisories/GHSA-86w9-cpqp-85rv" }]
]);
if (Object.keys(audit.vulnerabilities).length && new Date() >= new Date("2026-11-04T00:00:00Z")) {
  throw new Error("Review expired mobile build-tool advisory exceptions before release.");
}
const checked = new Set();
function verify(name, ancestors = new Set()) {
  if (ancestors.has(name)) return;
  const vulnerability = audit.vulnerabilities[name];
  if (!vulnerability || !vulnerability.via?.length) throw new Error(`Incomplete advisory chain: ${name}`);
  const next = new Set([...ancestors, name]);
  for (const cause of vulnerability.via) {
    if (typeof cause === "string") verify(cause, next);
    else if (exceptions.get(name)?.advisory !== cause.url) throw new Error(`Release blocked by ${name}: ${cause.url}`);
    else checked.add(name);
  }
}
for (const name of Object.keys(audit.vulnerabilities)) verify(name);
for (const name of checked) {
  const metadata = JSON.parse(await readFile(`${mobile}/node_modules/${name}/package.json`, "utf8"));
  if (metadata.version !== exceptions.get(name).version) throw new Error(`Review changed advisory exception: ${name}`);
}
async function sourceMaps(directory) {
  const files = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const path = `${directory}/${entry.name}`;
    if (entry.isDirectory()) files.push(...await sourceMaps(path));
    else if (entry.name.endsWith(".map")) files.push(path);
  }
  return files;
}
const maps = await sourceMaps(`${mobile}/dist/ios`);
if (!maps.length) throw new Error("Export the final iOS bundle with source maps before auditing runtime dependencies.");
function inspect(map) {
  for (const source of map.sources ?? []) {
    if (/node_modules[\\/](?:braces|node-forge)[\\/]/.test(source)) throw new Error(`Vulnerable build tool shipped in runtime: ${source}`);
  }
  for (const section of map.sections ?? []) inspect(section.map);
}
for (const path of maps) inspect(JSON.parse(await readFile(path, "utf8")));
console.log(`Mobile audit: ${Object.keys(audit.vulnerabilities).length} affected dependency paths; ${checked.size} dated upstream exceptions, absent from ${maps.length} iOS runtime source map(s).`);
