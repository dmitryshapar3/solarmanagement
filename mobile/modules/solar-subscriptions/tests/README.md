# Local StoreKit regression tests

This standalone XCTest host compiles the production `../ios/SolarSubscriptionStore.swift` through a relative Xcode file reference. It uses Apple's StoreKitTest framework and an active local `.storekit` configuration. It never installs or launches the Solar application, uses Solar credentials, enters an Apple Account, or makes a real payment. Test products are available only to this separate test host. Neither the generated project nor its configuration is imported into the release application.

Requirements: macOS with Xcode and an installed iOS Simulator runtime (iOS 18+), Xcode command-line tools selected, and Ruby with the `xcodeproj` gem. Use a separate simulator because Apple provides one shared StoreKit test environment. The test runner runs tests serially and clears its local simulated transactions between tests.

Run from any working directory:

```sh
xcrun simctl list devices available
/path/to/solar-management/mobile/modules/solar-subscriptions/tests/run-tests.sh <isolated-simulator-UDID>
```

For a macOS CI runner, install the `xcodeproj` Ruby gem and run `bash mobile/modules/solar-subscriptions/tests/run-ci.sh`. It selects an available iOS 18+ runtime and compatible iPhone model from `simctl` JSON, creates a fresh simulator, then deletes only that simulator after the run. No pre-existing simulator is reused or removed. Keep `.build` logs and result bundles as CI artifacts when diagnosing failure.

Compile the complete native app and actual Expo bridge separately with `bash mobile/modules/solar-subscriptions/tests/run-app-build.sh`, after `npm ci` in `mobile`. This requires Node 22.13+, CocoaPods and Xcode 26.4+ for Expo SDK 57. The script installs the locked Pods with `--deployment`, verifies that `SolarSubscriptionsModule.swift` is included in the autolinked native target, and performs an unsigned Release simulator build. It requires the application binary and bundled JavaScript and preserves Pod/build logs under `.build`. It does not archive, upload, sign with a developer account or publish the app.

Install CocoaPods 1.16.2 and Minitest 5.25.5 for this build script. Before installing Pods, it runs the 16-case Ruby checksum regression suite and rejects failures, errors, skips or missing tests. Expo's precompiled Core podspec embeds the absolute checkout location in its archive URL and preparation command. `mobile/ios/expo_core_checksum.rb` preserves the real archive paths and canonicalizes only those two checkout prefixes for the lockfile checksum, including when CocoaPods reloads a saved specification. Other podspec inputs and source-built modules keep their normal checksum behavior; precompiled-module settings remain unchanged.

The script generates `.build/SolarStoreKit.xcodeproj`, builds the independent simulator test host, ad-hoc signs it locally, then runs `xcodebuild test-without-building`. It exports `test-summary.json` and `test-results.json` through `xcresulttool` and requires exactly 11 passing tests, no failures/skips, and all expected test method names. A zero or partial run fails even if `xcodebuild` returns success. `.build/` is ignored and contains the JSON reports, build/test logs, DerivedData and timestamped `.xcresult` evidence. On failure, inspect `.build/build.log` or `.build/test.log`; do not enter Apple credentials if a test configuration problem presents an account dialog. Select the local StoreKit configuration rather than proceeding to the App Store. The generator explicitly attaches `SolarLocal.storekit` to this host's Run scheme.

Coverage:

- Both approved product IDs, one-month/one-year periods, localized USD prices and no additional Apple introductory trial. The one-calendar-month account trial is enforced by the server.
- Real local StoreKit monthly/yearly purchases, verified signed transactions and `appAccountToken` forwarding.
- Explicit restore while active and after expiration; disabling auto-renew preserves remaining access, then Apple's accelerated renewal clock naturally expires the subscription.
- Refund/revocation removes access; Ask to Buy remains locked until approval and the native callback delivers the verified update.
- Cancellation, unavailable products and invalid signatures do not grant new access. A catalog outage preserves an existing verified entitlement.
- Forced renewal retains the original transaction identity and refreshes the active verified transaction.
- A stable server UUID is mandatory before purchase; missing/invalid binding creates no transaction.
- Unfinished delivery survives restart until explicit acknowledgement after server verification. A foreign Solar account cannot finish the owner's record; repeating a correct acknowledgement is safe.

The previous nine-case version passed on October 1, 2026 against iOS Simulator 27.0. The current 11-case version and changed production Swift require a new macOS run; that previous result does not verify account binding or server receipt acknowledgement. Natural expiration uses Apple's `timeRate = .oneRenewalEveryTwoSeconds`; the SDK's forced-expiration helper left an older signed transaction active in repeated runs, so it is not used as the expiration fixture.

These checks validate local implementation behavior. App Store Connect product availability, paid-app agreements, production pricing, sandbox Apple Accounts, real payment-sheet presentation, grace-period timing and the Expo/React Native screen integration require their own checks. Local testing does not substitute for those environments or create subscriptions in App Store Connect.

The configuration's schema was adapted from Apple's [Understanding StoreKit workflows sample](https://developer.apple.com/documentation/storekit/understanding-storekit-workflows); only the two Solar test products remain. APIs and serial test-environment behavior follow [SKTestSession](https://developer.apple.com/documentation/storekittest/sktestsession), [setting up local StoreKit testing](https://developer.apple.com/documentation/xcode/setting-up-storekit-testing-in-xcode), and [testing across Xcode and sandbox environments](https://developer.apple.com/documentation/storekit/testing-at-all-stages-of-development-with-xcode-and-the-sandbox).
