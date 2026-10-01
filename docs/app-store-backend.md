# Backend pages for App Store release

The backend provides public, server-rendered pages at:

- Privacy Policy URL: `https://solar.dshapar.com/privacy`
- Support URL: `https://solar.dshapar.com/support`
- Support and privacy contact: `dmitry.shapar@gmail.com`

These routes allow anonymous access. The web login and navigation menu link to them. They do not change API authorization, account provisioning, billing enforcement, or the database schema. The app must also expose its privacy link; enter these URLs in App Store Connect after the backend deployment is verified.

## Build and deploy

From the repository root:

```sh
dotnet build src/DeyeSolar.Web/DeyeSolar.Web.csproj -c Release
```

Deploy the backend build containing `Pages/Privacy.cshtml`, `Pages/Support.cshtml`, `Pages/Shared/_PublicLayout.cshtml`, and `wwwroot/css/public.css`, together with the route and web-link changes. The existing Dockerfile publishes the complete ASP.NET Core project. Use the established production deployment process and verify its target before running the legacy scripts under `k8s`.

The application still needs its existing SQL Server connection and applies migrations during startup. These public-page changes introduce no migration. Keep production credentials in the existing deployment configuration.

## Verify the deployed pages

Run without cookies or an authorization header, and do not follow redirects:

```sh
solar_release_check_dir=$(mktemp -d)
curl --max-time 15 --fail --silent --show-error \
  --output "$solar_release_check_dir/privacy.html" --write-out 'privacy: %{http_code}\n' \
  https://solar.dshapar.com/privacy
curl --max-time 15 --fail --silent --show-error \
  --output "$solar_release_check_dir/support.html" --write-out 'support: %{http_code}\n' \
  https://solar.dshapar.com/support
rg -F '<h1>Privacy policy</h1>' "$solar_release_check_dir/privacy.html"
rg -F '<h1>DeyeSolar support</h1>' "$solar_release_check_dir/support.html"
rg -F 'mailto:dmitry.shapar@gmail.com' "$solar_release_check_dir/privacy.html" "$solar_release_check_dir/support.html"
curl --max-time 15 --silent --show-error --output /dev/null --write-out 'anonymous API: %{http_code}\n' \
  https://solar.dshapar.com/api/dashboard
```

Both pages must return **200** and contain their expected heading and contact link. The anonymous API must still return **401**. A 200 alone is insufficient: before these pages were implemented, the Blazor fallback returned 200 with a “Not found” page.

Open both URLs in a private browser window. Check that text is readable on a phone, the contact and navigation links work, and no login, bot challenge, or missing stylesheet blocks access. Then verify the links from the web login and signed-in navigation menu.

## Privacy and billing facts

The privacy page describes the shared installation, saved Deye/Shelly credentials and identifiers, rules, energy history, connected services, and data requests. It does not promise per-account installation isolation or encryption of database settings. Confirm the deployed hosting/network providers' logging and retention practices before completing App Store privacy disclosures; do not select “Data Not Collected” merely because the app has no advertising SDK.

The iOS subscription implementation uses Apple StoreKit to verify entitlements locally. Apple processes payment; the DeyeSolar server receives neither payment card/bank details nor subscription transaction records in the current implementation. This is not server-side subscription enforcement. If signed transactions, account binding, or App Store Server Notifications are added later, update the privacy text and App Store disclosures to match the new data flow.

For App Review, use the documented offline demo with synthetic data once it is integrated and validated, and describe it in review notes. Do not provide the real installation account: current backend authorization allows every authenticated user to access shared settings and control devices. The public pages do not create a protected reviewer account.

Apple references: [App Review Guidelines](https://developer.apple.com/app-store/review/guidelines/), [App Privacy Details](https://developer.apple.com/app-store/app-privacy-details/), and [standard Apple EULA](https://www.apple.com/legal/internet-services/itunes/dev/stdeula/).
