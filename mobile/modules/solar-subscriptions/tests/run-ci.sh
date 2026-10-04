#!/bin/bash
set -euo pipefail

script_root="$(cd -- "$(dirname -- "$0")" && pwd)"
xcodebuild -version
# Build an isolated simulator using an installed compatible runtime and device.
# Avoid hard-coded Xcode paths, model names and pre-existing runner simulators.
solar_storekit_selection="$(python3 - <<'PY'
import json
import subprocess
import sys

def simctl(*args):
    return json.loads(subprocess.check_output(['xcrun', 'simctl', *args, '--json']))

runtimes = [runtime for runtime in simctl('list', 'runtimes')['runtimes']
            if runtime.get('isAvailable') and 'iOS' in runtime.get('name', '')
            and int(runtime['version'].split('.')[0]) >= 18]
if not runtimes:
    raise SystemExit('An installed iOS 18+ simulator runtime is required.')
def version(runtime):
    return tuple(map(int, runtime['version'].split('.')))

# These runtimes have documented StoreKitTest regressions; Xcode 26.6 alone
# still produced SKInternalErrorDomain Code=3 on the iOS 26.5 simulator.
# https://developer.apple.com/forums/thread/826971
# https://developer.apple.com/forums/thread/808030
excluded = [runtime for runtime in runtimes if (26, 2) <= version(runtime) < (26, 6)]
if excluded:
    print('Excluded affected StoreKitTest runtimes: ' + json.dumps([
        {key: runtime[key] for key in ('identifier', 'name', 'version')} for runtime in excluded
    ]), file=sys.stderr)
runtimes = [runtime for runtime in runtimes if runtime not in excluded]
if not runtimes:
    raise SystemExit('No compatible iOS 18+ StoreKitTest runtime remains. '
                     'iOS 26.2-26.5 is excluded due to documented simulator regressions. '
                     'Install the fixed iOS 26.6 runtime; tests must not be skipped.')
runtime = max(runtimes, key=version)
version = runtime['version'].split('.')
runtime_version = sum(int(part) << shift for part, shift in zip(version + ['0'] * (3 - len(version)), (16, 8, 0)))
devices = [device for device in simctl('list', 'devicetypes')['devicetypes']
           if device.get('productFamily') == 'iPhone'
           and device.get('minRuntimeVersion', 0) <= runtime_version
           and device.get('maxRuntimeVersion', 0xFFFFFFFF) >= runtime_version]
if not devices:
    raise SystemExit('No iPhone device type supports the installed iOS runtime.')
print('Selected StoreKit simulator inputs: ' + json.dumps({
    'runtime': {key: runtime[key] for key in ('identifier', 'name', 'version')},
    'device': {key: devices[-1][key] for key in ('identifier', 'name')}
}), file=sys.stderr)
print(runtime['identifier'])
print(devices[-1]['identifier'])
PY
)"
solar_storekit_runtime="$(printf '%s\n' "$solar_storekit_selection" | sed -n '1p')"
solar_storekit_device="$(printf '%s\n' "$solar_storekit_selection" | sed -n '2p')"
solar_storekit_simulator="$(xcrun simctl create "SolarStoreKit-${GITHUB_RUN_ID:-local}-$$" "$solar_storekit_device" "$solar_storekit_runtime")"
cleanup() {
  xcrun simctl shutdown "$solar_storekit_simulator" >/dev/null 2>&1 || true
  xcrun simctl delete "$solar_storekit_simulator" >/dev/null 2>&1 || true
}
trap cleanup EXIT
printf 'Selected isolated StoreKit simulator UDID: %s\n' "$solar_storekit_simulator"
xcrun simctl boot "$solar_storekit_simulator"
xcrun simctl bootstatus "$solar_storekit_simulator" -b
bash "$script_root/run-tests.sh" "$solar_storekit_simulator"
