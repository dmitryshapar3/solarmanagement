# Local StoreKit regression tests

This standalone XCTest host compiles the production `../ios/SolarSubscriptionStore.swift` through a relative Xcode file reference. It uses Apple's StoreKitTest framework and an active local `.storekit` configuration. It never installs or launches the Solar application, uses Solar credentials, enters an Apple Account, or makes a real payment. Test products are available only to this separate test host. Neither the generated project nor its configuration is imported into the release application.

Requirements: macOS with Xcode and an installed iOS Simulator runtime (iOS 18+), Xcode command-line tools selected, and Ruby with the `xcodeproj` gem. Use a separate simulator because Apple provides one shared StoreKit test environment. The test runner runs tests serially and clears its local simulated transactions between tests.

Run from any working directory:

```sh
xcrun simctl list devices available
/path/to/solar-management/mobile/modules/solar-subscriptions/tests/run-tests.sh <isolated-simulator-UDID>
```

The script generates `.build/SolarStoreKit.xcodeproj`, builds the independent simulator test host, ad-hoc signs it locally, then runs `xcodebuild test-without-building`. `.build/` is ignored and contains build/test logs, DerivedData and timestamped `.xcresult` evidence. On failure, inspect `.build/build.log` or `.build/test.log`; do not enter Apple credentials if a test configuration problem presents an account dialog. Select the local StoreKit configuration rather than proceeding to the App Store. The generator explicitly attaches `SolarLocal.storekit` to this host's Run scheme.

Coverage:

- Both approved product IDs, one-month/one-year periods, localized USD prices and genuine eligible two-week introductory offers.
- Real local StoreKit monthly/yearly purchases, verified signed transactions and `appAccountToken` forwarding.
- Explicit restore while active and after expiration; disabling auto-renew preserves remaining access, then Apple's accelerated renewal clock naturally expires the subscription.
- Refund/revocation removes access; Ask to Buy remains locked until approval and the native callback delivers the verified update.
- Cancellation, unavailable products and invalid signatures do not grant new access. A catalog outage preserves an existing verified entitlement.
- Forced renewal retains the original transaction identity and refreshes the active verified transaction.

On October 1, 2026, this portable harness passed all nine XCTest cases against iOS Simulator 27.0 with the production Swift source (2.62 seconds of test bodies). Natural expiration uses Apple's `timeRate = .oneRenewalEveryTwoSeconds`; the SDK's forced-expiration helper left an older signed transaction active in repeated runs, so it is not used as the expiration fixture.

These checks validate local implementation behavior. App Store Connect product availability, paid-app agreements, production pricing, sandbox Apple Accounts, real payment-sheet presentation, grace-period timing and the Expo/React Native screen integration require their own checks. Local testing does not substitute for those environments or create subscriptions in App Store Connect.

The configuration's schema was adapted from Apple's [Understanding StoreKit workflows sample](https://developer.apple.com/documentation/storekit/understanding-storekit-workflows); only the two Solar test products remain. APIs and serial test-environment behavior follow [SKTestSession](https://developer.apple.com/documentation/storekittest/sktestsession), [setting up local StoreKit testing](https://developer.apple.com/documentation/xcode/setting-up-storekit-testing-in-xcode), and [testing across Xcode and sandbox environments](https://developer.apple.com/documentation/storekit/testing-at-all-stages-of-development-with-xcode-and-the-sandbox).
