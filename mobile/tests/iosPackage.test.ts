import assert from "node:assert/strict";
import { test } from "node:test";
import { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { createRequire } from "node:module";
import { spawnSync } from "node:child_process";

const mobileRoot = resolve(__dirname, "..");
const root = resolve(mobileRoot, "..");
const checker = join(root, "scripts/check-ios-package.py");
const expo = JSON.parse(readFileSync(join(mobileRoot, "app.json"), "utf8")).expo;
const purposeKeys = ["NSMotionUsageDescription", "NSLocationWhenInUseUsageDescription", "NSFaceIDUsageDescription"];
const languages: string[] = expo.ios.infoPlist.CFBundleLocalizations;
const requireMobile = createRequire(join(mobileRoot, "package.json"));

function xml(value: unknown): string {
  if (typeof value === "string") return `<string>${value.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")}</string>`;
  if (typeof value === "boolean") return value ? "<true/>" : "<false/>";
  if (Array.isArray(value)) return `<array>${value.map(xml).join("")}</array>`;
  assert.ok(value && typeof value === "object");
  return `<dict>${Object.entries(value).map(([key, item]) => `<key>${key}</key>${xml(item)}`).join("")}</dict>`;
}
function writePlist(path: string, value: unknown) {
  writeFileSync(path, `<?xml version="1.0" encoding="UTF-8"?><plist version="1.0">${xml(value)}</plist>`);
}
function fixture(run: (directory: string, app: string, info: Record<string, unknown>) => void) {
  const directory = mkdtempSync(join(tmpdir(), "solar-ios-package-"));
  const app = join(directory, "DeyeSolar.app");
  mkdirSync(app);
  const info = { ...structuredClone(expo.ios.infoPlist), CFBundleIdentifier: expo.ios.bundleIdentifier,
    CFBundleShortVersionString: expo.version, CFBundleVersion: expo.ios.buildNumber };
  try {
    writePlist(join(app, "Info.plist"), info);
    for (const language of languages) {
      const catalog = JSON.parse(readFileSync(join(root, "i18n", `${language}.json`), "utf8"));
      const localized = Object.fromEntries(purposeKeys.map(key => {
        assert.equal(typeof expo.ios.infoPlist[key], "string", `${key} is declared in source`);
        assert.equal(typeof catalog[expo.ios.infoPlist[key]], "string", `${language} translates ${key}`);
        return [key, catalog[expo.ios.infoPlist[key]]];
      }));
      mkdirSync(join(app, `${language}.lproj`));
      writePlist(join(app, `${language}.lproj/InfoPlist.strings`), localized);
    }
    run(directory, app, info);
  } finally { rmSync(directory, { recursive: true, force: true }); }
}
function check(...args: string[]) {
  const result = spawnSync("python3", ["-B", checker, ...args], { encoding: "utf8" });
  assert.equal(result.error, undefined, "Python packaging preflight can run");
  return result;
}

test("actual configured Expo iOS mod composition retains native purposes without always-location access", async () => {
  const { withPlugins } = requireMobile("@expo/config-plugins");
  const config = withPlugins({ ...structuredClone(expo), _internal: { projectRoot: mobileRoot } }, expo.plugins);
  // Execute the real registered Info.plist mod chain, never filesystem-writing dangerous mods.
  const result = await config.mods.ios.infoPlist({ ...config, modRawConfig: structuredClone(expo),
    modResults: { ...structuredClone(expo.ios.infoPlist), NSLocationAlwaysUsageDescription: "old setting",
      NSLocationAlwaysAndWhenInUseUsageDescription: "old setting" },
    modRequest: { projectRoot: mobileRoot, platformProjectRoot: join(mobileRoot, "ios"), platform: "ios", modName: "infoPlist" } });
  for (const key of purposeKeys) assert.equal(result.modResults[key], expo.ios.infoPlist[key], `${key} survives real plugins`);
  assert.equal(result.modResults.NSLocationAlwaysUsageDescription, undefined);
  assert.equal(result.modResults.NSLocationAlwaysAndWhenInUseUsageDescription, undefined);
  assert.ok(!(result.modResults.UIBackgroundModes ?? []).includes("location"));
});

test("processed app passes with all three purposes and all fifteen packaged translations", () => {
  fixture((_directory, app) => {
    const result = check("--app", app);
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(JSON.parse(result.stdout), { ok: true, CFBundleIdentifier: expo.ios.bundleIdentifier,
      CFBundleShortVersionString: expo.version, CFBundleVersion: expo.ios.buildNumber, localizations: 15, permissionPurposes: 3 });
  });
});

test("missing default motion purpose fails even when every localized file contains it", () => {
  fixture((_directory, app, info) => {
    delete info.NSMotionUsageDescription;
    writePlist(join(app, "Info.plist"), info);
    const result = check("--app", app);
    assert.equal(result.status, 1);
    assert.match(result.stderr, /Built Info.plist is missing a nonempty NSMotionUsageDescription/);
  });
});

test("packaged missing, blank and outdated localized purposes fail independently of source catalogs", () => {
  fixture((_directory, app) => {
    const locale = "ru";
    const catalog = JSON.parse(readFileSync(join(root, "i18n", `${locale}.json`), "utf8"));
    const original = Object.fromEntries(purposeKeys.map(key => [key, catalog[expo.ios.infoPlist[key]]]));
    for (const value of [undefined, "  ", "old permission description"]) {
      const changed = { ...original };
      if (value === undefined) delete changed.NSMotionUsageDescription; else changed.NSMotionUsageDescription = value;
      writePlist(join(app, `${locale}.lproj/InfoPlist.strings`), changed);
      const result = check("--app", app);
      assert.equal(result.status, 1);
      assert.match(result.stderr, /ru.lproj\/InfoPlist.strings.*NSMotionUsageDescription/);
    }
  });
});

test("missing packaged language and blank primary purpose cannot pass a correct source manifest", () => {
  fixture((_directory, app, info) => {
    info.NSMotionUsageDescription = "  ";
    writePlist(join(app, "Info.plist"), info);
    assert.equal(check("--app", app).status, 1);
    info.NSMotionUsageDescription = expo.ios.infoPlist.NSMotionUsageDescription;
    writePlist(join(app, "Info.plist"), info);
    rmSync(join(app, "ko.lproj"), { recursive: true });
    const result = check("--app", app);
    assert.equal(result.status, 1);
    assert.match(result.stderr, /ko.lproj/);
  });
});

test("package identity uses the source build by default and permits an explicit retained-build check", () => {
  fixture((_directory, app, info) => {
    info.CFBundleVersion = "999";
    writePlist(join(app, "Info.plist"), info);
    assert.match(check("--app", app).stderr, /Built CFBundleVersion/);
    assert.equal(check("--app", app, "--expected-build", "999").status, 0);
    info.CFBundleIdentifier = "example.wrong.app";
    writePlist(join(app, "Info.plist"), info);
    const result = check("--app", app, "--expected-build", "999");
    assert.equal(result.status, 1);
    assert.match(result.stderr, /Built CFBundleIdentifier/);
  });
});

test("unexpected always and background location declarations are rejected", () => {
  fixture((_directory, app, info) => {
    info.NSLocationAlwaysUsageDescription = "unexpected";
    writePlist(join(app, "Info.plist"), info);
    assert.match(check("--app", app).stderr, /Unexpected always-location permission/);
    delete info.NSLocationAlwaysUsageDescription;
    info.UIBackgroundModes = ["location"];
    writePlist(join(app, "Info.plist"), info);
    assert.match(check("--app", app).stderr, /Unexpected background-location capability/);
  });
});

test("archive and exported IPA validate the processed primary app and binary localization plists", () => {
  fixture((directory, app) => {
    const archive = join(directory, "Fixture.xcarchive");
    const ipa = join(directory, "Fixture.ipa");
    const packageResult = spawnSync("python3", ["-B", "-c", `
import pathlib,plistlib,shutil,sys,zipfile
app,archive,ipa=map(pathlib.Path,sys.argv[1:])
for path in app.rglob('InfoPlist.strings'):
 path.write_bytes(plistlib.dumps(plistlib.loads(path.read_bytes()),fmt=plistlib.FMT_BINARY))
(archive/'Products/Applications').mkdir(parents=True)
shutil.copytree(app,archive/'Products/Applications'/app.name)
with zipfile.ZipFile(ipa,'w') as package:
 for path in app.rglob('*'):
  if path.is_file():package.write(path,'Payload/'+app.name+'/'+path.relative_to(app).as_posix())
`, app, archive, ipa], { encoding: "utf8" });
    assert.equal(packageResult.status, 0, packageResult.stderr);
    const archiveResult = check("--archive", archive);
    const ipaResult = check("--ipa", ipa);
    assert.equal(archiveResult.status, 0, archiveResult.stderr);
    assert.equal(ipaResult.status, 0, ipaResult.stderr);
  });
});
