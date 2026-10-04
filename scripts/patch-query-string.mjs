import { readFile, writeFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";

// React Navigation 7 imports named functions; query-string 9 defaults to a namespace.
// Preserve both contracts while using the fixed ESM URI decoder. Fail on package drift.
const root = new URL("../mobile/node_modules/query-string/", import.meta.url);
const metadata = JSON.parse(await readFile(new URL("package.json", root), "utf8"));
const path = fileURLToPath(new URL("index.js", root));
const original = "import * as queryString from './base.js';\n\nexport default queryString;\n";
const patched = original + "\nexport * from './base.js';\n";
const content = await readFile(path, "utf8");
if (metadata.version !== "9.5.1" || ![original, patched].includes(content)) {
  throw new Error("Review the query-string navigation compatibility patch before changing dependencies.");
}
if (content !== patched) await writeFile(path, patched);
console.log("Verified query-string 9.5.1 default and named navigation exports.");
