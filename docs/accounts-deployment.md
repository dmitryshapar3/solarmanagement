# Accounts, verification and private installations

Each new account owns a new, empty installation. Its Deye/Shelly credentials, devices, settings, rules, readings, forecast caches and sales observations are isolated from other installations. Configure integrations and the solar site in Settings after registration. For measured PV history/comparison, explicitly confirm that the saved selected inverter reports DC PV power; changing the inverter requires confirming its source again. Settings includes draft **Test** actions: a test does not save credentials or switch a socket.

Existing users and private data migrate together into the `legacy` installation, preserving their existing access. Back up SQL Server before this upgrade. Startup applies the EF migrations and imports existing provider connections into the dynamic integration runtime. Duplicate normalized email addresses or phone numbers stop the migration with a fixed diagnostic; resolve those identities deliberately before retrying. An image-only rollback is unsafe after provider import, even while `legacy` is the only installation; see the recovery requirements below.

## Delivery providers

This deployment uses **email and Google**; SMS remains disabled. Copy [accounts.env.example](accounts.env.example) to the server secret file described below and fill only the email and Google fields. Keep `Auth__RegistrationEnabled=false` until the upgraded application and provider setup have been checked. Registration defaults to enabled when the setting is missing or invalid, so retain the explicit boolean.

For email, create a [Resend sending domain](https://resend.com/docs/dashboard/domains/introduction), add its required DNS records and wait for verification. Create a [sending-access API key](https://resend.com/docs/dashboard/api-keys/introduction) restricted to that domain. Set `Auth__Email__ResendApiKey` and `Auth__Email__From` to that key and an address on the verified domain. A separate SMTP server is unnecessary. Provider secrets belong only on the server, never in Git, a mobile build or the application's editable Settings.

Set `Auth__Google__ClientId` and `Auth__Google__ClientSecret` using the web OAuth client described below. Leave all three `Auth__Phone__*` values empty to keep Twilio Verify SMS unavailable. Do not supply placeholder keys: provider availability checks configuration presence, not whether the external account actually works.

Each login method is available only when its required configuration exists. Existing password login remains available without provider keys. `Auth__RegistrationEnabled` controls email/phone registration and is the default policy for Google registration. The optional `Auth__Google__RegistrationEnabled` boolean overrides only Google account creation. Neither setting disables sign-in or linking for existing accounts. Email registration verifies a code before account creation; email sign-in codes work only for an existing confirmed identity. New passwords require 12–128 characters. Verification expires after ten minutes, is single use and has resend/attempt limits.

## Google and linked identities

Create a **Web application** OAuth client in [Google Cloud](https://developers.google.com/identity/protocols/oauth2/web-server), configure its consent screen, and register this exact authorized redirect URI:

```text
https://solar.dshapar.com/signin-google
```

The native app opens the backend's Google flow in the system authentication browser; it exchanges a short-lived ticket with PKCE and checks callback state. The app scheme is `deyesolar://auth/callback`; Google redirects to the HTTPS server, not directly to the app scheme.

Accounts are not silently merged because email addresses match. Sign in to the existing account, then use **Settings → Linked sign-in methods** on mobile or **Account** on the web to verify/link Google, email or phone. This preserves the account's installation and prevents identity takeover through an unverified email match.

When Google registration is enabled, a verified Google email without an existing account creates a new account with its own empty installation. An email already owned by an account requires signing in to that account before linking, even when registration is closed. Linking from a username-only account adds and confirms its Google email atomically with the provider login. A different existing email is preserved; an email or Google identity owned by another account is rejected. Locked accounts cannot mutate linked identities.

The mobile linking flow retains the original authenticated bearer through both the link request and PKCE exchange. It must not use the anonymous sign-in session replacement path. Backend restart invalidates pending Google proofs and mobile sessions, so start again after signing in. The corrected native client is build `1.0.0 (8)` and requires a new Xcode archive/TestFlight upload; redeploying the backend does not update an installed binary.

## Production configuration

The existing production service is `deye-solar` in `/opt/projects/compose.yml`. It already loads `./secrets/deye-solar.env`; preserve that file and its connection settings. Place the completed example in `/opt/projects/secrets/deye-solar-accounts.env`, owned by the deployment administrator with mode `0600`, outside the repository. Append it to the existing service's list:

```yaml
services:
  deye-solar:
    env_file:
      - ./secrets/deye-solar.env
      - ./secrets/deye-solar-accounts.env
```

This is a fragment to merge into the existing service, not a replacement Compose file. Preserve its image selection, database connection, network, resource limits and volumes. A Compose `.env` file alone does not forward variables into a container. Inline `environment` values override `env_file`, so check for conflicting `Auth__*` entries. Authentication configuration is captured at startup before the SQL settings provider; saving application Settings or changing an environment file without recreating the container does not reload it.

The existing service sets `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`. Change that entry in its **existing `environment` block** to `"false"`; the example environment file cannot override the inline value. This disables ASP.NET's automatic forwarding so the application's explicit proxy policy controls client IP handling. Set `Auth__TrustedProxyAddresses` only to the actual reverse proxy IP addresses, separated by commas, and configure that proxy to overwrite `X-Forwarded-For`. Do not substitute a guessed Docker gateway or trust arbitrary clients. Leaving it empty with automatic forwarding disabled applies authentication limits to the proxy peer as a whole. The application fixes OAuth scheme/host using `Auth__PublicBaseUrl`, which must be an HTTPS origin without a path, query or fragment.

Preserve the existing mapping `deye-dataprotection:/root/.aspnet/DataProtection-Keys` and named Docker volume `projects_deye-dataprotection`. The example sets `Auth__DataProtectionKeysPath` to this same absolute path. Do not replace the volume, change the Compose project name or point at an empty key directory during this upgrade. Keep the key files restricted and include them in protected backups; the current key is root-owned with mode `0600`.

The current backend also requires the two signed integration packages before importing existing Deye and Shelly connections. Follow the [integration deployment instructions](../integrations/README.md) and use [Build-IntegrationDeployment.ps1](../tools/Build-IntegrationDeployment.ps1) with `-SigningKeyPath`, `-PublicKeyPath`, a new `-OutputDirectory` and `-IncludeWeb`. Retain the established publisher key pair across releases. The output contains the Web publish, `integration-bundle/deye.cloud-1.0.0.zip`, `integration-bundle/shelly.cloud-1.0.0.zip`, the publisher public key and `integration-bootstrap.json`. The private signing key stays outside the repository, image, bundle and server runtime.

For the existing `/opt/projects/compose.yml` service, add these mounts alongside the unchanged authentication volume:

| Host path | Container path | Access |
| --- | --- | --- |
| `/opt/projects/ops/releases/<release>/integration-bundle` | `/app/integration-bundle` | Read-only |
| `/opt/projects/ops/releases/<release>/integration-bootstrap.json` | `/app/integration-bootstrap.json` | Read-only |
| `/opt/projects/data/deye-solar/integration-packages` | `/data/integration-packages` | Persistent, writable |
| `/opt/projects/data/deye-solar/integration-keys` | `/data/keys/integrations` | Persistent, writable |

Replace `<release>` with the deployed release directory. These container paths match the build tool's defaults: `InstalledBundleDirectory`, `InstalledPackageDirectory` and `InstalledKeyRingDirectory`. The generated bootstrap configuration pins archive hashes, the trusted publisher public-key file and approved provider origins. It is operator configuration loaded before editable SQL settings. Restrict the writable directories and back up the installed package catalog/artifacts and integration keyring with SQL and the existing authentication keyring. Deleting integration keys makes imported credentials unreadable; replacing the authentication volume invalidates its protected state. Publishing the Web application alone does not include the provider workers.

Run exactly one application instance. Verification challenges, Google tickets and mobile sessions currently live in one server process, even though Data Protection keys are persistent. Restart requires mobile sign-in again and invalidates pending verification/OAuth exchanges. Shared state would be required before adding replicas. This template is for the existing server: a fresh installation with registration disabled also needs deliberate administrator provisioning through `Auth__BootstrapAdminPassword`; it contains no default administrator password.

## Deployment and verification

1. Record the current image identity and configuration, and make a verified SQL backup before starting the upgraded image. Preserve the existing secret file and authentication Data Protection volume; retain any existing integration packages and integration keyring in the same protected recovery set. Check for duplicate normalized email addresses/phone numbers and phone values exceeding 450 UTF-16 code units before migration; report counts without exposing identities. Startup applies `AddInstallations`, `DynamicProviderIntegrations` and `DynamicIntegrationOAuth` even while registration is disabled, assigning existing accounts and private data to `legacy` and preparing provider storage.
2. Prepare and verify the signed packages, public key and generated bootstrap configuration. Fill and protect the email/Google environment file, merge the Compose changes above, and validate with `docker compose -f /opt/projects/compose.yml config --quiet`. Do not publish the resolved Compose configuration or container environment: they contain secrets. Keep `Auth__RegistrationEnabled=false` and verify that exact nonsecret value in the actual process configuration; `/api/auth/options` alone cannot prove the flag is false when all provider keys are missing.
3. Deploy the intended image by recreating only `deye-solar`; keep a single instance. Verify all three migrations, both installed signed provider packages, completed legacy import, persistent encryption keys, existing password login, preserved settings/history and unchanged adjacent services. Confirm the enabled Deye/Shelly instances use the expected package hashes, credentials are encrypted, selected inverter/PV confirmation and historical device identities agree, and fresh inverter/socket reads work. With configured providers, `/api/auth/options` must report email and Google enabled, phone disabled and registration disabled. This endpoint reports configuration availability, not successful delivery.
4. Verify email delivery and Google linking/sign-in with an authorized existing account on the deployed HTTPS origin. Confirm the expected real proxy client IP reaches authentication rate limiting. Automated provider tests use controlled transports and fake verification delivery; they do not prove live provider setup.
5. For Google-only new accounts, retain `Auth__RegistrationEnabled=false`, set the literal `Auth__Google__RegistrationEnabled=true` in the existing Compose environment mapping or protected environment file, and recreate only Solar. `/api/auth/options` must then report `registrationEnabled=false` and `googleRegistrationEnabled=true`; existing email sign-in remains available. To additionally open email/phone registration, set `Auth__RegistrationEnabled=true`. Verify effective inline overrides and the options endpoint. Register two controlled accounts and confirm their initially empty installations, credentials, devices, settings, rules and history remain separate from each other and from `legacy`. Draft integration **Test** actions must not save settings or switch sockets.
6. Update the mobile build for these authentication flows and sign in again after the backend restart. Complete live email and Google registration/login checks before treating those methods as available to users. Keep SMS disabled.

Legacy import removes the SQL `DeyeCloud:AppSecret`, `DeyeCloud:Password` and `Shelly:AuthKey` secret rows transactionally. Later tenant runtime initialization may reseed those canonical setting names with empty typed defaults; their presence alone is not a failed cutover. Verify that no nonempty plaintext values remain in those SQL settings, encrypted integration configurations exist, and the completed import marker prevents fallback to deployment credentials. After verifying the imported connections, remove obsolete provider credentials from the deployment environment while preserving database/account settings and protected recovery backups.

## Rollback

An image-only rollback is unsafe as soon as legacy provider import completes, even if public registration stayed disabled and only `legacy` exists. The cutover encrypts credentials into new integration configurations, removes their old plaintext settings and maps selected device/history/rule identifiers to internal device keys. An older image cannot reconstruct those connections from the migrated database. EF migration `Down` alone cannot restore the removed credentials or safely undo the device mapping.

Additional installations introduce a separate isolation risk: an old application ignores `InstallationId` and can expose or mix private settings and readings. EF's ownership `Down` guard rejects that downgrade, but cannot prevent someone starting an old image directly.

Prefer a forward fix compatible with both installation ownership and dynamic integrations. Returning to an older image requires a deliberate, coordinated recovery using matched SQL, image/Compose, secrets, package artifacts and keyring backups. Quiesce application writes and account for registrations, observations and commands since the backup before restoring. Do not restore automatically or run old and new application instances against the same database. Preserve the failing database, both keyrings and installed packages for diagnosis; a pre-upgrade SQL restore discards subsequent accounts and telemetry. Keeping registration disabled limits new-account exposure but does not make image-only rollback safe.

## Device names and power balance

One Shelly Cloud key discovers that Shelly account's devices. Cloud names come from Shelly; **Devices → Edit name** stores an installation-local display name and **Use Shelly name** restores the cloud name. The legacy cutover maps device identifiers and rule targets together, preserving device ownership, rule configuration and cloud switch state.

The dashboard shows battery charging/discharging power. Inverter details calculate the signed residual `PV + grid + battery − load` with Deye's convention: grid export and battery charging are negative. A positive residual can include conversion losses, rounding or readings taken at different times; it is an estimate, not a measured inverter-loss sensor. Missing/stale readings are marked accordingly.
