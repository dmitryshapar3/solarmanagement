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
  -derivedDataPath "$script_root/.build/DerivedData" CODE_SIGNING_ALLOWED=YES CODE_SIGN_IDENTITY=- \
  > "$script_root/.build/build.log" 2>&1
codesign --verify --deep --strict "$script_root/.build/DerivedData/Build/Products/Debug-iphonesimulator/SolarStoreKitHost.app"
# There is one StoreKit test environment: run serially, never in parallel.
solar_storekit_xctestrun=("$script_root"/.build/DerivedData/Build/Products/SolarStoreKit_*.xctestrun)
if [[ ${#solar_storekit_xctestrun[@]} -ne 1 || ! -f "${solar_storekit_xctestrun[0]}" ]]; then
  echo 'Expected one built StoreKit xctestrun file. Remove stale .build output and retry.' >&2
  exit 1
fi
solar_storekit_result="$script_root/.build/LocalStoreKit-$(date -u +%Y%m%dT%H%M%SZ).xcresult"
solar_storekit_test_exit=0
xcodebuild test-without-building -xctestrun "${solar_storekit_xctestrun[0]}" \
  -destination "platform=iOS Simulator,id=$solar_storekit_simulator" \
  -parallel-testing-enabled NO -test-timeouts-enabled YES \
  -default-test-execution-time-allowance 30 -maximum-test-execution-time-allowance 45 \
  -resultBundlePath "$solar_storekit_result" \
  > "$script_root/.build/test.log" 2>&1 || solar_storekit_test_exit=$?
if [[ "$solar_storekit_test_exit" -ne 0 ]]; then
  echo 'xcodebuild test failure output (full log is retained in the native artifact):' >&2
  tail -n 200 "$script_root/.build/test.log" >&2
fi
xcrun xcresulttool get test-results summary --path "$solar_storekit_result" \
  > "$script_root/.build/test-summary.json"
xcrun xcresulttool get test-results tests --path "$solar_storekit_result" \
  > "$script_root/.build/test-results.json"
python3 - "$script_root/.build/test-summary.json" "$script_root/.build/test-results.json" <<'PY'
import json
import sys

with open(sys.argv[1], encoding='utf-8') as source:
    summary = json.load(source)
with open(sys.argv[2], encoding='utf-8') as source:
    results = json.load(source)
expected = {
    'test01CatalogContainsBothPlansWithoutASecondIntroductoryTrial',
    'test02MonthlyPurchaseRestoreAndCancellation',
    'test03YearlyPurchaseAndRevocation',
    'test04PendingAskToBuyDoesNotGrantUntilApproved',
    'test05UserCancellationDoesNotGrantAccess',
    'test06CatalogFailureDisablesPurchaseButPreservesVerifiedAccess',
    'test07UnverifiedTransactionFailsClosed',
    'test08ForcedRenewalKeepsOriginalPurchaseAndUpdatesVerifiedTransaction',
    'test09NaturalExpirationWithAcceleratedAppleClockAndRestore',
    'test10PurchaseRequiresStableServerAccountIdentity',
    'test11UnfinishedDeliverySurvivesRestartUntilCorrectAccountAcknowledgesIt'
}
counts = {key: summary.get(key) for key in ('totalTestCount', 'passedTests', 'failedTests', 'skippedTests')}
if counts != {'totalTestCount': 11, 'passedTests': 11, 'failedTests': 0, 'skippedTests': 0}:
    print('StoreKit XCTest failure summary:')
    print(json.dumps(summary, indent=2))
    print('StoreKit XCTest test results:')
    print(json.dumps(results, indent=2), flush=True)
    raise SystemExit(f'Expected 11 passing StoreKit tests with no failures/skips; actual: {counts}')
serialized = json.dumps(results)
missing = sorted(name for name in expected if name not in serialized)
if missing:
    raise SystemExit(f'Expected StoreKit tests were not discovered: {missing}')
print('Verified 11 expected StoreKit tests: 11 passed, 0 failed, 0 skipped.')
PY
if [[ "$solar_storekit_test_exit" -ne 0 ]]; then
  echo "xcodebuild exited with $solar_storekit_test_exit; inspect .build/test.log." >&2
  exit "$solar_storekit_test_exit"
fi
printf 'StoreKit local tests passed. Result: %s\n' "$solar_storekit_result"
