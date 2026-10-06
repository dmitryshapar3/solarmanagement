# SmartSolar Mobile

React Native / Expo client for the existing Solar server. Settings offers 15 interface languages shared with the web dashboard; selections work offline and synchronize with the account after the updated backend is deployed. See [localization](../docs/localization.md).

## Run

```powershell
cd mobile
npm ci
npm start
```

The default server is `https://solar.dshapar.com`. Sign in with the existing Solar account. A custom server URL can be entered on the login screen.

The four tabs are Home, Energy, Devices and Automations. Home combines measured PV, battery charge and energy flows. Energy contains Production and Export: expected weather ranges are separate from measured PV; completed export totals exclude the provisional current hour. Automations includes recorded Activity, paused client presets and a guarded editor. Readings log and grouped checks retain unavailable values as gaps. Pull down to refresh each screen and its nested data; there are no generic refresh buttons. A purposeful missing-price check remains available in Export.

Light is the initial appearance. Settings offers Light, Dark and System, a collapsed language dropdown, installation preferences and account security. Fonts are bundled Onest and Unbounded; native Apple sign-in appears only when both the device and configured server support it. The iOS tab bar uses Callstack native tabs and follows the selected UIKit appearance; other platforms use the shared floating tab bar. Development builds expose the component gallery by holding the Home account avatar.

The sample account stays local, uses clearly disclosed fictional measurements and disables hardware commands. Real manual switching preserves durable command IDs, pending/uncertain handling and observed state; an enabled controlling automation requires an explicit pause or just-once choice.

Focused screens refresh every five minutes while the app is in the foreground. Missing values are not displayed as zero. Financial values come from the server; the app does not recalculate settlement prices or revenue.

Backgrounding and foreground access checks retain the current screen while the server-confirmed grant is valid. If access expires, connected screens close and the last navigation state remains in account-scoped memory for restoration after successful verification. Logout and account changes clear that state.

## Verify

Use Node 22.15.0 or another version supported by the pinned React Native dependencies.

```powershell
npm ci
npm run typecheck
npm test
npx expo-doctor
npm run export:ios
```

The iOS export verifies the JavaScript bundle and assets. It does not compile, sign or test an iOS binary. Native Keychain, charts, navigation and app lifecycle behavior still require an iPhone or simulator check. The repository workflow runs the type check, permanent regression tests and iOS bundle export.

Expo Doctor reports an app-config synchronization warning because this repository intentionally contains the native Xcode project as well as `app.json`. Native plugin/configuration changes must be applied and reviewed using the prebuild command below; Xcode alone does not sync them.

## Build in Xcode and upload to TestFlight

Use the existing checkout on the Mac. Install Xcode, Node.js 22.15.0 (or a compatible Node 22 release) and CocoaPods. Open Xcode once to finish installing its components and accepting its license. Sign in with the Apple Developer account in Xcode Settings > Accounts.

From the repository root:

```bash
bash mobile/prepare-xcode.command
```

The script installs locked JavaScript dependencies, runs the mobile checks, installs CocoaPods dependencies and opens `mobile/ios/DeyeSolar.xcworkspace`. It uses the checked-in native project when available. No Expo account or EAS cloud build is required. Expo remains an application dependency.

For a simulator run, select an installed iPhone simulator as the run destination and keep `npm start` running in `mobile`, then press **Run**. Xcode 27 displays simulators in **Device Hub**. A simulator does not require an Apple Developer team. Keep Xcode's local code signing enabled: an unsigned simulator build can fail to access the Keychain used for sign-in.

To build a standalone simulator app with its JavaScript bundled, run from `mobile/ios`:

```bash
xcodebuild -workspace DeyeSolar.xcworkspace -scheme DeyeSolar \
  -configuration Release -sdk iphonesimulator \
  -destination 'generic/platform=iOS Simulator' \
  -derivedDataPath build/DerivedData CODE_SIGN_IDENTITY=- build
```

Install `build/DerivedData/Build/Products/Release-iphonesimulator/DeyeSolar.app` in a booted simulator with `xcrun simctl install booted <app-path>`, then launch it with `xcrun simctl launch booted com.dshapar.solar`. This Release build does not need Metro. Use `ios/.xcode.env.local` for a machine-specific `NODE_BINARY` path when Node is not available to Xcode's shell; that file is ignored by Git.

1. Select the **DeyeSolar** target, then **Signing & Capabilities**. Enable automatic signing and choose the Apple Developer team. The bundle identifier is `com.dshapar.solar`.
2. For a Debug run, keep `npm start` running in a separate terminal in `mobile`, then run on an iPhone or simulator. A physical iPhone must be able to reach that development server. Check sign-in, foreground/background refresh, tab navigation, generation dates, signed/unknown revenue and logout. Device switching sends real commands to configured devices. Release archives bundle the app and do not require this development server.
3. In App Store Connect, create the iOS app record with the same bundle identifier if it does not already exist.
4. Select **Any iOS Device** as the build destination and choose **Product > Archive**. In Organizer choose **Distribute App > App Store Connect > Upload**.
5. After Apple finishes processing, open the app's **TestFlight** page and add your Apple ID as an internal tester. Install the build through TestFlight on the iPhone. This does not publish a public App Store release.

The SmartSolar redesign build is `1.0.0 (12)`. Each account receives a one-calendar-month trial, with one socket allowed when adding devices. At expiry the server denies unpaid reading/control, and the app hides cached connected screens. StoreKit purchasing stays disabled until the backend Apple configuration is valid. The offline demo remains free and uses fictional data without controlling real equipment. See [subscription integration and prerequisites](modules/solar-subscriptions/README.md) and [backend configuration](../docs/app-store-backend.md).

Client and server use the current contracts together. There are no old-session migrations, permissive old response formats, or unversioned rule mutations. Deploy the matching backend before validating connected features of this build. The complete native harness and Release bridge compilation are required; live Apple sandbox purchases remain a separate acceptance check.

Increment the build number for each subsequent upload. Keep the native target and `app.json` values aligned. Do not commit signing credentials or local Xcode user data. When changing Expo native plugins, apply the configuration with `npx expo prebuild --platform ios --no-clean --no-install`, inspect the native diff, then run `pod install` again. Do not overwrite local signing changes without reviewing them.

Native compilation, signing and TestFlight upload must be completed on the Mac; a successful JavaScript export alone does not establish those results. See Apple's [upload guide](https://developer.apple.com/help/app-store-connect/manage-builds/upload-builds/).

## Backend API

The app uses bearer tokens from `POST /api/auth/login`. On iOS the session is stored in Keychain through SecureStore and bound to the exact configured endpoint. Passwords are not persisted. The app reads only the endpoint-bound current secure-session format. Web preview sessions remain in memory only. Native requests use `expo/fetch` to reject redirects before forwarding credentials.

The dashboard uses `/api/dashboard` and `/api/dashboard/refresh`; generation uses `/api/solar/estimate` and `/api/solar/history`; sales uses `/api/sales`. These routes require authenticated access and preserve the web server's time-zone, calculation and accounting rules.

Historical readings require explicit measurement validity flags; rule-run SOC and power can be absent. The connected app and server must be deployed from the same current contract.
