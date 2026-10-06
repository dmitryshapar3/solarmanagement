#!/usr/bin/env python3
"""Check processed iOS package metadata before distributing a native build.

Reads a built .app, an .xcarchive, or an exported IPA without modifying it.
Code signing and Apple processing remain separate release checks.
"""
import argparse
import json
from pathlib import Path
import plistlib
import sys
import zipfile

ROOT = Path(__file__).resolve().parent.parent
LANGUAGES = ("en", "ru", "uk", "pl", "de", "fr", "es", "it", "pt", "nl", "cs", "tr", "zh", "ja", "ko")
PURPOSE_KEYS = ("NSMotionUsageDescription", "NSLocationWhenInUseUsageDescription", "NSFaceIDUsageDescription")
ALWAYS_KEYS = ("NSLocationAlwaysUsageDescription", "NSLocationAlwaysAndWhenInUseUsageDescription")


class PackageError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise PackageError(message)


def plist(raw, name):
    try:
        value = plistlib.loads(raw)
    except (ValueError, plistlib.InvalidFileException) as error:
        raise PackageError(f"Invalid processed plist: {name}") from error
    require(isinstance(value, dict), f"Expected a plist dictionary: {name}")
    return value


def nonempty(value):
    return isinstance(value, str) and bool(value.strip())


def validate(read, expected_build=None, expected_version=None):
    info = plist(read("Info.plist"), "Info.plist")
    # Validate the artifact first: source declarations alone cannot prove packaging.
    for key in PURPOSE_KEYS:
        require(nonempty(info.get(key)), f"Built Info.plist is missing a nonempty {key}")
    for key in ALWAYS_KEYS:
        require(key not in info, f"Unexpected always-location permission: {key}")
    require("location" not in info.get("UIBackgroundModes", []), "Unexpected background-location capability")

    source = json.loads((ROOT / "mobile/app.json").read_text())["expo"]
    ios = source["ios"]
    expected = {
        "CFBundleIdentifier": ios["bundleIdentifier"],
        "CFBundleShortVersionString": expected_version or source["version"],
        "CFBundleVersion": expected_build or ios["buildNumber"],
    }
    for key, value in expected.items():
        require(info.get(key) == value, f"Built {key} is {info.get(key)!r}; expected {value!r}")
    require(set(ios["infoPlist"].get("CFBundleLocalizations", [])) == set(LANGUAGES),
            "Source must declare the supported 15 iOS localizations")
    require(set(info.get("CFBundleLocalizations", [])) == set(LANGUAGES),
            "Built package must declare the supported 15 iOS localizations")
    purposes = {}
    for key in PURPOSE_KEYS:
        phrase = ios["infoPlist"].get(key)
        require(nonempty(phrase), f"Source does not declare {key}")
        require(info[key] == phrase, f"Built {key} differs from the source purpose")
        purposes[key] = phrase
    for language in LANGUAGES:
        name = f"{language}.lproj/InfoPlist.strings"
        localized = plist(read(name), name)
        catalog = json.loads((ROOT / "i18n" / f"{language}.json").read_text())
        for key, phrase in purposes.items():
            translated = catalog.get(phrase)
            require(nonempty(translated), f"Missing {language} catalog purpose: {key}")
            require(nonempty(localized.get(key)), f"Built {name} is missing a nonempty {key}")
            require(localized[key] == translated, f"Built {name} has an outdated {key}")
    return {**expected, "permissionPurposes": len(PURPOSE_KEYS), "localizations": len(LANGUAGES)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    artifact = parser.add_mutually_exclusive_group(required=True)
    artifact.add_argument("--app", type=Path, help="Processed built .app directory")
    artifact.add_argument("--archive", type=Path, help="Signed or unsigned .xcarchive directory")
    artifact.add_argument("--ipa", type=Path, help="Exported IPA; read without extraction")
    parser.add_argument("--expected-build", help="Override source build number for a retained package")
    parser.add_argument("--expected-version", help="Override source marketing version")
    args = parser.parse_args()
    try:
        if args.ipa:
            with zipfile.ZipFile(args.ipa) as package:
                primary = [name for name in package.namelist()
                           if name.startswith("Payload/") and name.endswith(".app/Info.plist") and name.count("/") == 2]
                require(len(primary) == 1, "IPA must contain exactly one primary application")
                prefix = primary[0][:-len("Info.plist")]
                result = validate(lambda name: package.read(prefix + name), args.expected_build, args.expected_version)
        else:
            app = args.app
            if args.archive:
                apps = list((args.archive / "Products/Applications").glob("*.app"))
                require(len(apps) == 1, "Archive must contain exactly one primary application")
                app = apps[0]
            result = validate(lambda name: (app / name).read_bytes(), args.expected_build, args.expected_version)
        print(json.dumps({"ok": True, **result}, sort_keys=True))
    except (PackageError, OSError, KeyError, zipfile.BadZipFile, json.JSONDecodeError) as error:
        print(f"iOS package preflight failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
