#!/bin/bash
set -euo pipefail

cd -- "$(dirname -- "$0")"

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "Run this script on your Mac with Xcode installed."
  exit 1
fi

if ! command -v node >/dev/null || ! command -v npm >/dev/null; then
  echo "Install Node.js 22.15.0 or a compatible Node 22 release, then run this script again."
  exit 1
fi

node -e 'const [major, minor] = process.versions.node.split(".").map(Number); if (major !== 22 || minor < 13) { console.error("Use Node.js 22.13 or newer in the Node 22 line (tested: 22.15.0)."); process.exit(1); }'

if ! xcrun --find xcodebuild >/dev/null 2>&1 || ! xcodebuild -version >/dev/null 2>&1; then
  echo "Install and open Xcode, accept its license and select it under Xcode Settings > Locations > Command Line Tools."
  exit 1
fi

if ! command -v pod >/dev/null; then
  echo "Install CocoaPods (for example: brew install cocoapods), then run this script again."
  exit 1
fi

npm ci
npm run typecheck
npm test

if [[ ! -f ios/DeyeSolar.xcodeproj/project.pbxproj ]]; then
  npx expo prebuild --platform ios --no-install
fi

(
  cd ios
  pod install
)

echo "Opening Xcode. Select your Apple Developer team in Signing & Capabilities."
open ios/DeyeSolar.xcworkspace
