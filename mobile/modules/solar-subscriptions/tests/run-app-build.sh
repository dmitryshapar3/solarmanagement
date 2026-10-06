#!/bin/bash
set -euo pipefail

script_root="$(cd -- "$(dirname -- "$0")" && pwd)"
mobile_root="$(cd -- "$script_root/../../.." && pwd)"
mkdir -p "$script_root/.build"
if [[ "$(uname -s)" != "Darwin" ]]; then
  echo 'The native application build requires macOS and Xcode 26.4 or later.' >&2
  exit 1
fi
node -e 'const [major, minor] = process.versions.node.split(".").map(Number); if (major !== 22 || minor < 13) { console.error("Use Node.js 22.13 or newer in the Node 22 line."); process.exit(1); }'
ruby "$script_root/expo_core_checksum_test.rb"
# The SDK checks its own minimum Xcode version during CocoaPods installation.
# --deployment preserves the checked-in dependency contract instead of updating it.
(
  cd "$mobile_root/ios"
  pod _1.16.2_ install --deployment
) > "$script_root/.build/pod-install.log" 2>&1
ruby -rxcodeproj - "$mobile_root/ios/Pods/Pods.xcodeproj" <<'RUBY'
project = Xcodeproj::Project.open(ARGV.fetch(0))
bridge = project.targets.find { |target| target.name == 'SolarSubscriptions' }
raise 'SolarSubscriptions native target was not autolinked' unless bridge
files = bridge.source_build_phase.files_references.map { |reference| reference.path }
raise 'SolarSubscriptions Expo bridge was not included' unless files.any? { |path| File.basename(path) == 'SolarSubscriptionsModule.swift' }
RUBY
xcodebuild -workspace "$mobile_root/ios/DeyeSolar.xcworkspace" -scheme DeyeSolar \
  -configuration Release -sdk iphonesimulator -destination 'generic/platform=iOS Simulator' \
  -derivedDataPath "$script_root/.build/AppDerivedData" CODE_SIGNING_ALLOWED=NO build \
  > "$script_root/.build/app-build.log" 2>&1
solar_storekit_app="$script_root/.build/AppDerivedData/Build/Products/Release-iphonesimulator/DeyeSolar.app"
if [[ ! -f "$solar_storekit_app/DeyeSolar" || ! -f "$solar_storekit_app/main.jsbundle" ]]; then
  echo 'The simulator application binary and its JavaScript bundle were not built.' >&2
  exit 1
fi
python3 "$mobile_root/../scripts/check-ios-package.py" --app "$solar_storekit_app" \
  | tee "$script_root/.build/app-package-preflight.json"
printf 'Native Solar application and Expo subscription bridge compiled. App: %s\n' "$solar_storekit_app"
