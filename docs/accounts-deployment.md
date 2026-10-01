# Accounts, verification and private installations

Each new account owns a new, empty installation. Its Deye/Shelly credentials, devices, settings, rules, readings, forecast caches and sales observations are isolated from other installations. Configure integrations and the solar site in Settings after registration. For measured PV history/comparison, explicitly confirm that the saved selected inverter reports DC PV power; changing the inverter requires confirming its source again. Settings includes draft **Test** actions: a test does not save credentials or switch a socket.

Existing users and private data migrate together into the `legacy` installation, preserving their existing access. Back up SQL Server before this upgrade. Startup applies the EF migration. Duplicate normalized email addresses or phone numbers stop the migration with a fixed diagnostic; resolve those identities deliberately before retrying. The downgrade is refused after additional installations exist.

## Delivery providers

The implementation uses [Resend](https://resend.com/docs/api-reference/emails/send-email) for verification email and [Twilio Verify](https://www.twilio.com/docs/verify/api) for SMS verification. A separate SMTP server is unnecessary. Create provider accounts, verify the sending domain with Resend, and create a Twilio Verify service. Provider keys belong in server secrets, never in Git or a mobile build.

Pass these environment variables to the running backend container/process:

```text
Auth__RegistrationEnabled=true
Auth__PublicBaseUrl=https://solar.dshapar.com
Auth__Email__ResendApiKey=<Resend API key>
Auth__Email__From=DeyeSolar <login@your-verified-domain.example>
Auth__Phone__AccountSid=<Twilio account SID>
Auth__Phone__AuthToken=<Twilio auth token>
Auth__Phone__VerifyServiceSid=<Verify service SID starting with VA>
Auth__Google__ClientId=<Google web OAuth client ID>
Auth__Google__ClientSecret=<Google web OAuth client secret>
```

Each delivery/login method is enabled only when its required configuration exists. Existing password login remains available without these provider keys. Email/phone registration verifies a code before creating an account; email/phone sign-in codes work only for an existing confirmed identity. New passwords require at least 12 characters. Verification expires after ten minutes, is single use and has resend/attempt limits.

## Google and linked identities

Create a **Web application** OAuth client in [Google Cloud](https://developers.google.com/identity/protocols/oauth2/web-server), configure its consent screen, and register this exact authorized redirect URI:

```text
https://solar.dshapar.com/signin-google
```

The native app opens the backend's Google flow in the system authentication browser; it exchanges a short-lived ticket with PKCE and checks callback state. The app scheme is `deyesolar://auth/callback`; Google redirects to the HTTPS server, not directly to the app scheme.

Accounts are not silently merged because email addresses match. Sign in to the existing account, then use **Settings → Linked sign-in methods** on mobile or **Account** on the web to verify/link Google, email or phone. This preserves the account's installation and prevents identity takeover through an unverified email match.

## Deployment and verification

1. Back up the database and deploy this branch/PR's backend image.
2. Pass provider secrets into the actual process/container environment. A Compose `.env` file alone does not automatically forward variables to the container; use `environment`/`env_file`, or Kubernetes Secrets.
3. Preserve ASP.NET Data Protection keys across restarts and replicas. Set `Auth__DataProtectionKeysPath` to an absolute directory mounted on persistent, restricted storage (for example `/var/lib/deyesolar/keys`). Verification challenges, Google tickets and mobile sessions currently live in one server process: restart requires sign-in again. Run one application instance, or add shared stores before scaling to multiple replicas.
4. Behind a reverse proxy, set `Auth__TrustedProxyAddresses` to its actual IP addresses, separated by commas, and configure that proxy to overwrite `X-Forwarded-For`. Only those proxies can supply the client IP for authentication rate limits; host/scheme headers are not trusted. Without this setting the limit applies to the proxy peer as a whole.
5. Verify email/SMS delivery and Google login on the deployed HTTPS origin. Create two test accounts, configure each separately, and confirm devices/settings/history never cross accounts.
6. On mobile, update the TestFlight build and sign in again after the server upgrade if its previous session was revoked by restart.

Automated provider tests use controlled transports and fake verification delivery. They do not demonstrate that an unconfigured Resend/Twilio/Google account can send messages or finish live OAuth.

## Device names and power balance

One Shelly Cloud key discovers that Shelly account's devices. Cloud names come from Shelly; **Devices → Edit name** stores an installation-local display name and **Use Shelly name** restores the cloud name. Device IDs, rules and cloud switch state are preserved.

The dashboard shows battery charging/discharging power. Inverter details calculate the signed residual `PV + grid + battery − load` with Deye's convention: grid export and battery charging are negative. A positive residual can include conversion losses, rounding or readings taken at different times; it is an estimate, not a measured inverter-loss sensor. Missing/stale readings are marked accordingly.
