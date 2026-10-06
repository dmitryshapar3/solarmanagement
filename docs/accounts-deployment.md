# Accounts, verification and private installations

Each new account owns a new, empty installation. Its Deye/Shelly credentials, devices, settings, rules, readings, forecast caches and sales observations are isolated from other installations. Configure Connections and Solar site settings after registration. For measured PV history/comparison, explicitly confirm that the saved selected inverter reports DC PV power; changing the inverter requires confirming its source again. Connections includes draft **Check connection** actions, and Solar site settings has **Test weather here**. These checks do not save credentials or switch a socket.

There is no shared default installation or automatic import of provider credentials. Registration and explicit administrator provisioning create a unique installation for each account. Provider connections are configured through the signed-package integration setup flow.

## Delivery providers

This deployment uses **email and Google**; SMS remains disabled. Copy [accounts.env.example](accounts.env.example) to the server secret file described below and fill only the email and Google fields. Keep `Auth__RegistrationEnabled=false` until the application and provider setup have been checked. Registration defaults to enabled when the setting is missing or invalid, so retain the explicit boolean.

For email, create a [Resend sending domain](https://resend.com/docs/dashboard/domains/introduction), add its required DNS records and wait for verification. Create a [sending-access API key](https://resend.com/docs/dashboard/api-keys/introduction) restricted to that domain. Set `Auth__Email__ResendApiKey` and `Auth__Email__From` to that key and an address on the verified domain. A separate SMTP server is unnecessary. Provider secrets belong only on the server, never in Git, a mobile build or the application's editable Settings.

Set `Auth__Google__ClientId` and `Auth__Google__ClientSecret` using the web OAuth client described below. Leave all three `Auth__Phone__*` values empty to keep Twilio Verify SMS unavailable. Do not supply placeholder keys: provider availability checks configuration presence, not whether the external account actually works.

Each login method is available only when its required configuration exists. Existing password login remains available without provider keys. `Auth__RegistrationEnabled` controls email/phone registration and is the default policy for Google registration. The optional `Auth__Google__RegistrationEnabled` boolean overrides only Google account creation. Neither setting disables sign-in or linking for existing accounts. The redesigned code flow verifies a code, then signs in to the matching confirmed account or creates a new account when registration is enabled. The older explicit registration and existing-account code endpoints remain supported for released clients. New passwords require 12–128 characters. Verification expires after ten minutes, is single use and has resend/attempt limits.

## Google and linked identities

Create a **Web application** OAuth client in [Google Cloud](https://developers.google.com/identity/protocols/oauth2/web-server), configure its consent screen, and register this exact authorized redirect URI:

```text
https://solar.dshapar.com/signin-google
```

The native app opens the backend's Google flow in the system authentication browser; it exchanges a short-lived ticket with PKCE and checks callback state. The app scheme is `deyesolar://auth/callback`; Google redirects to the HTTPS server, not directly to the app scheme.

Accounts are not silently merged because email addresses match. Sign in to the existing account, then use **Settings → Sign-in & security** on mobile or **Settings → Account** (`/settings/account`) on the web to manage linked sign-in methods and verify replacement contacts. This preserves the account's installation and prevents identity takeover through an unverified email match.

When Google registration is enabled, a verified Google email without an existing account creates a new account with its own empty installation. An email already owned by an account requires signing in to that account before linking, even when registration is closed. Linking from a username-only account adds and confirms its Google email atomically with the provider login. A different existing email is preserved; an email or Google identity owned by another account is rejected. Locked accounts cannot mutate linked identities.

The mobile linking flow retains the original authenticated bearer through both the link request and PKCE exchange. Backend restarts preserve durable mobile sessions; pending short-lived Google proofs must be restarted after an interrupted exchange.

## Production configuration

### Sign in with Apple

Apple account authentication uses separate credentials from App Store subscription verification. Enable **Sign in with Apple** for the explicit native App ID `com.dshapar.solar`, include the capability in its distribution provisioning profile, and group the web Services ID with that primary App ID. Register the exact HTTPS return URL `https://solar.dshapar.com/auth/apple/callback` for the Services ID. Native authentication exchanges its Apple identity token, one-time authorization code and raw nonce with the backend; the backend verifies the SHA-256 nonce, RSA signature, issuer, audience and expiry before creating or linking an account.

Operator-only settings are `Auth__Apple__Enabled`, `Auth__Apple__NativeClientId`, `Auth__Apple__ServicesId`, `Auth__Apple__TeamId`, `Auth__Apple__KeyId`, `Auth__Apple__PrivateKeyFile` and `Auth__Apple__CallbackUrl`. Mount the Sign in with Apple P-256 private key as a protected readable secret file; do not put it in Git, editable application settings or mobile builds. `PrivateKeyPem` is also supported for a secret injection mechanism. Public `/api/auth/options` exposes `appleEnabled` and `appleWebEnabled` only when the respective configuration is complete and the signing key is usable. Missing configuration leaves Apple unavailable without disabling existing Google, password or code sign-in.

Apple refresh tokens are encrypted with the persistent **auth** data-protection keyring. Unlinking Apple and deleting an account atomically enqueue encrypted revocation work in an independent durable outbox; a retry worker contacts Apple's revocation endpoint. Preserve both the SQL outbox and auth keyring in matched backups. Do not remove the auth key merely because the owning account has been deleted. Keep the Apple operator key available until outstanding revocations are complete. Provider subjects identify accounts; matching email never merges an Apple account into an existing account.

### Account management compatibility

The additive `/api/account` routes provide profile display name, personal display timezone, public session IDs with scoped revocation, verified contact replacement, unlinking and a bounded ZIP export. Cookie mutations require antiforgery; mobile bearer requests use the existing session validation. Contact replacement keeps the old verified contact active until delivery proof completes and then rechecks account/contact version and uniqueness. Unlinking refuses to remove the last usable configured sign-in method.

Fresh proof may use the current password, a delivered security code or an opaque external-provider proof. External proofs expire quickly, are single use and bind to the initiating account, durable session, security stamp and requested operation. The old `/api/auth/security` JSON export, password/code requests, Google PKCE exchange and build 11 session contracts remain supported. The new ZIP contains account data and configuration/history only for installations owned by that account; it omits password hashes, sessions, provider credentials and encrypted Apple revocation material. Exports exceeding bounded archive/history limits return `export_too_large` and require a separate support export.

Use the [production operations guide](production-operations.md) for Compose/Kubernetes, SQL privileges, signed packages, durable key volumes and backup/restore. Deploy the migration job with its privileged connection, then start the Production application with the restricted runtime connection and `Operations__DatabaseMode=validate`. An HTTP runtime cannot apply migrations.

Place the completed [accounts.env.example](accounts.env.example) outside the repository, owned by the deployment administrator with mode `0600`, and add it to the application's Compose `env_file` list. Keep provider secrets out of application Settings. Inline `environment` values override `env_file`; remove conflicting values and validate Compose without printing resolved secrets. Configuration changes require recreating the container.

Set `Auth__DataProtectionKeysPath=/data/keys/auth` in a persistent volume writable by the application UID. Keep installed provider artifacts and the integration keyring persistent as well. Build the signed provider release with [Build-IntegrationDeployment.ps1](../tools/Build-IntegrationDeployment.ps1); mount its immutable bundle and bootstrap configuration read-only. The private signing key stays outside the image and runtime.

Disable `ASPNETCORE_FORWARDEDHEADERS_ENABLED` and configure `Auth__TrustedProxyAddresses` with only the actual reverse proxy IP addresses. The proxy must overwrite `X-Forwarded-For` and `X-Forwarded-Proto`. `Auth__PublicBaseUrl` must be the deployed HTTPS origin without a path, query or fragment.

On a fresh database with registration closed, supply `Auth__BootstrapAdminPasswordFile` during the migration job to provision `admin`. The account, its new installation and its owner membership are committed atomically. There is no default password, inherited solar location, provider connection or shared legacy installation. Omit bootstrap provisioning when public registration is the intended first-account flow.

## Deployment and verification

1. Build and verify the signed provider release; protect the email/Google secrets and validate Compose with `config --quiet`.
2. Run the migration job against a fresh SQL database and start one application owner. Confirm `/health/ready` and `/health/live`, the installed package catalog and bootstrap administrator login if enabled.
3. Verify email delivery and Google linking/sign-in on the deployed HTTPS origin. Check that authentication rate limits receive the actual proxy client IP. Controlled test transports do not verify live provider setup.
4. Enable Google-only registration with `Auth__RegistrationEnabled=false` and `Auth__Google__RegistrationEnabled=true`, or enable email/phone registration with `Auth__RegistrationEnabled=true`. Recreate the container and confirm `/api/auth/options` reports the intended methods. Availability reports configuration presence, not external delivery success.
5. Register two controlled accounts and confirm their credentials, devices, settings, rules and history remain separate. Connect providers explicitly through integration setup; draft **Test** actions must not save settings or switch sockets. Configure each solar site before using generation estimates.
6. Confirm a mobile session survives an application restart and is rejected after explicit revocation. Complete live email and Google checks before opening registration to users.

## Recovery

Prefer forward fixes. Restore a matched SQL, release image, signed package artifacts, operator configuration and both protection keyrings as described in the [production operations guide](production-operations.md). Stop writes before taking a coordinated backup and before migration. Never start an older schema-incompatible image or run two automation owners against the same database. Database `Down` is not an account-data recovery procedure.

## Device names and power balance

One Shelly Cloud key discovers that Shelly account's devices. Cloud names come from Shelly; **Devices → Edit name** stores an installation-local display name and **Use Shelly name** restores the cloud name. Rules and provider bindings use the same installation-local internal device identity.

The dashboard shows battery charging/discharging power. Inverter details calculate the signed residual `PV + grid + battery − load` with Deye's convention: grid export and battery charging are negative. A positive residual can include conversion losses, rounding or readings taken at different times; it is an estimate, not a measured inverter-loss sensor. Missing/stale readings are marked accordingly.
