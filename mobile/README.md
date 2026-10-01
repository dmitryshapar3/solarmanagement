# DeyeSolar Mobile

React Native / Expo client for the existing Solar server. All app screens use English.

## Run

```powershell
cd mobile
npm ci
npm start
```

The default server is `https://solar.dshapar.com`. Sign in with the existing Solar account. A custom server URL can be entered on the login screen.

The Home screen combines battery SOC, solar generation and grid power with a forced refresh button. Both the live generation metric and the generation chart use PV production, independently of battery charging or discharging power. Generation shows the estimated range and actual readings. Sales shows the server's completed-period estimates and the separate provisional current hour. Both have dedicated detail tabs. Devices, rules, history and account settings remain available.

Focused screens refresh every five minutes while the app is in the foreground. Missing values are not displayed as zero. Financial values come from the server; the app does not recalculate settlement prices or revenue.

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

The current TestFlight pilot is `1.0.0 (2)`. Connected access requires server sign-in and is available without a purchase: `src/application/releaseConfig.ts` keeps subscription enforcement disabled. The existing free Open-Meteo backend configuration remains in use; no server redeployment is required for this pilot. The optional offline demo uses fictional, in-memory data and never controls real equipment.

StoreKit code and App Store drafts are retained for a later paid release. See [subscription integration and prerequisites](modules/solar-subscriptions/README.md) before enabling billing in a subsequent build. App Store publication is deferred.

Increment the build number for each subsequent upload. Keep the native target and `app.json` values aligned. Do not commit signing credentials or local Xcode user data. When changing Expo native plugins, apply the configuration with `npx expo prebuild --platform ios --no-clean --no-install`, inspect the native diff, then run `pod install` again. Do not overwrite local signing changes without reviewing them.

Native compilation, signing and TestFlight upload must be completed on the Mac; a successful JavaScript export alone does not establish those results. See Apple's [upload guide](https://developer.apple.com/help/app-store-connect/manage-builds/upload-builds/).

## Backend API

The app uses bearer tokens from `POST /api/auth/login`. On iOS the session is stored in Keychain through SecureStore and bound to the exact configured endpoint. Passwords are not persisted. Older plaintext sessions require one new sign-in and are removed during startup. Web preview sessions remain in memory only. Native requests use `expo/fetch` to reject redirects before forwarding credentials.

The dashboard uses `/api/dashboard` and `/api/dashboard/refresh`; generation uses `/api/solar/estimate` and `/api/solar/history`; sales uses `/api/sales`. These routes require authenticated access and preserve the web server's time-zone, calculation and accounting rules.
