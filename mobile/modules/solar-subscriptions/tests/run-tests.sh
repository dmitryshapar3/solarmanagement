#!/bin/bash
set -euo pipefail

script_root="$(cd -- "$(dirname -- "$0")" && pwd)"
solar_storekit_simulator="${1:-${SOLAR_STOREKIT_SIMULATOR_ID:-}}"
if [[ -z "$solar_storekit_simulator" ]]; then
  echo 'Usage: ./run-tests.sh <isolated iOS Simulator UDID>' >&2
  exit 2
fi
ruby "$script_root/generate_project.rb"
xcodebuild build-for-testing \
  -project "$script_root/.build/SolarStoreKit.xcodeproj" -scheme SolarStoreKit \
  -configuration Debug -sdk iphonesimulator \
  -destination "platform=iOS Simulator,id=$solar_storekit_simulator" \
  -derivedDataPath "$script_root/.build/DerivedData" CODE_SIGNING_ALLOWED=NO \
  > "$script_root/.build/build.log" 2>&1
codesign --force --deep --sign - "$script_root/.build/DerivedData/Build/Products/Debug-iphonesimulator/SolarStoreKitHost.app"
codesign --verify --deep --strict "$script_root/.build/DerivedData/Build/Products/Debug-iphonesimulator/SolarStoreKitHost.app"
# There is one StoreKit test environment: run serially, never in parallel.
solar_storekit_xctestrun=("$script_root"/.build/DerivedData/Build/Products/SolarStoreKit_*.xctestrun)
if [[ ${#solar_storekit_xctestrun[@]} -ne 1 || ! -f "${solar_storekit_xctestrun[0]}" ]]; then
  echo 'Expected one built StoreKit xctestrun file. Remove stale .build output and retry.' >&2
  exit 1
fi
solar_storekit_result="$script_root/.build/LocalStoreKit-$(date -u +%Y%m%dT%H%M%SZ).xcresult"
xcodebuild test-without-building -xctestrun "${solar_storekit_xctestrun[0]}" \
  -destination "platform=iOS Simulator,id=$solar_storekit_simulator" \
  -parallel-testing-enabled NO -test-timeouts-enabled YES \
  -default-test-execution-time-allowance 30 -maximum-test-execution-time-allowance 45 \
  -resultBundlePath "$solar_storekit_result" \
  > "$script_root/.build/test.log" 2>&1
printf 'StoreKit local tests passed. Result: %s\n' "$solar_storekit_result"
