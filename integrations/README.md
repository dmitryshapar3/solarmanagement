# Independently installed integration workers

See [supported providers and socket source links](SupportedProviders.md) for the new manufacturer projects, access requirements, setup and protocol verification. The deployment script discovers and separately packages all provider projects.

The Web application references contracts and the runtime, not provider assemblies. Build and sign an adapter independently of the Web image. Only an operator can install executable packages. Never put the publisher private key in the application image, repository, package, or runtime configuration.

For a deployment that already has Deye or Shelly settings, prepare the built-in signed packages before upgrading the Web application. Generate the signing key once outside the repository and deployment output, then retain that publisher identity for subsequent releases:

```powershell
dotnet run --project tools/SolarManagement.IntegrationPackager -- --generate-key /secure/publisher-private-key.pem /secure/publisher-public-key.pem
./tools/Build-IntegrationDeployment.ps1 -SigningKeyPath /secure/publisher-private-key.pem -PublicKeyPath /secure/publisher-public-key.pem -OutputDirectory /build/solar-release -IncludeWeb
```

The output contains `web`, `integration-bundle` and an `integration-bootstrap.json` operator configuration file. Deploy the bundle read-only at `/app/integration-bundle`, the generated configuration alongside the Web application, durable writable artifacts at `/data/integration-packages`, and integration credential protection keys at `/data/keys/integrations`. Docker and Kubernetes mount `/data/keys` persistently and set `Integrations:KeyRingPath` to that integration subdirectory; the authentication keyring also uses the durable parent directory. Override `InstalledBundleDirectory`, `InstalledPackageDirectory` and `InstalledKeyRingDirectory` when preparing a Windows or different container deployment. The signing key is excluded. Trust configuration must come from the deployment file or environment, before editable SQL configuration is loaded.

This release packages Deye and Shelly as `1.0.4`; the other eight providers use `1.0.3`. Build each release once and retain its exact signed archives and generated bootstrap configuration for later deployments. Re-signing changes the archive digest even when the provider source is unchanged, so rebuilding an already published package requires a new version in both its manifest and descriptor. Each connection explicitly pins its installed signed package; package selection and recovery use the current integration manager.

The migration job verifies configured archive digests and publisher signatures before changing SQL. Each account creates provider connections explicitly through integration setup; credentials are encrypted in installation-scoped configurations and device bindings receive internal identities. Deployment credentials and historic device aliases are never imported automatically.

```powershell
dotnet publish integrations/SolarManagement.Providers.DeyeCloud -c Release -o ./artifacts/deye-worker
dotnet run --project tools/SolarManagement.IntegrationPackager -- ./artifacts/deye-worker ./integrations/manifests/deye.cloud.json /secure/publisher-private-key.pem ./artifacts/deye.cloud-1.0.4.zip
```

The final line is the exact SHA256 authorized by the installation request. Configure `IntegrationRuntime:PackageDirectory` on durable storage, the publisher PEM public key under `TrustedPublisherPublicKeys:solar-management`, and the operator-approved origins separately. Deye uses its EU/US HTTPS origins; Shelly uses `https://*.shelly.cloud`. The signed manifest cannot grant origins absent from host policy. Add no production credential to package templates.

For ongoing releases, give each package an immutable new version and use the operator package installation endpoint with its exact SHA256. A provider signed by an already trusted publisher can be added without restarting or redeploying the host: a platform operator first approves its new exact HTTPS origin through the authenticated origin approval endpoint, then installs its signed package. User integration managers and packages cannot approve their own network permissions. Live approvals are additive, bounded, normalized and stored in a protected atomic file beside the package catalog; they survive restart. Wildcard, local, IP, custom-port and URL-path approvals are rejected. The worker transport still checks public DNS addresses and disallows redirects. Publisher trust remains deployment-controlled; adding a new signer requires a deliberate operator trust configuration change.

Keep previous artifacts, the protected origin policy and the encryption keyring when backing up or restoring; persisted instances require their pinned package digest. A missing or corrupted approval file must never be replaced with approvals supplied by a package or public SQL settings. Built-in bootstrap archives can remain configured because repeated installation verifies and reuses the same immutable version. Do not replace a signed archive in place under an existing version, or rotate the trusted publisher key without retaining the prior public key under its prior publisher identifier. There is no live revocation claim: remove affected connections and prepare a coordinated policy change before withdrawing permissions.

Installing executable code requires the persisted Identity role `PlatformOperator`; an installation's `Owner` role does not grant it. An authorized database administrator can provision a specific existing account once with the following transaction. Replace the normalized user name with the intended account; do not grant this role to every installation owner.

```sql
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @UserId nvarchar(450) = (SELECT Id FROM AspNetUsers WHERE NormalizedUserName = N'ADMIN');
IF @UserId IS NULL THROW 50000, 'The chosen operator account does not exist.', 1;
DECLARE @RoleId nvarchar(450) = (SELECT Id FROM AspNetRoles WITH (UPDLOCK, HOLDLOCK) WHERE NormalizedName = N'PLATFORMOPERATOR');
IF @RoleId IS NULL
BEGIN
    SET @RoleId = CONVERT(nvarchar(36), NEWID());
    INSERT AspNetRoles (Id, Name, NormalizedName, ConcurrencyStamp)
    VALUES (@RoleId, N'PlatformOperator', N'PLATFORMOPERATOR', CONVERT(nvarchar(36), NEWID()));
END;
IF NOT EXISTS (SELECT 1 FROM AspNetUserRoles WITH (UPDLOCK, HOLDLOCK) WHERE UserId = @UserId AND RoleId = @RoleId)
    INSERT AspNetUserRoles (UserId, RoleId) VALUES (@UserId, @RoleId);
COMMIT;
```

Authenticate that account normally, then send `POST /api/v2/integration-packages/install` with an authenticated bearer session and JSON `{ "archivePath": "/operator-upload/provider-1.1.0.zip", "expectedSha256": "THE_EXACT_ARCHIVE_SHA256" }`. The archive path is local to the server and must contain a separately signed package from an already trusted publisher. Cookie requests also require the application's request verification token. Existing connections stay pinned; refresh the provider list to create a connection with the new version or explicitly select an installed version for an existing connection.

For a new provider network address, the same persisted platform operator can first call `POST /api/v2/integration-packages/approved-origins` with `{ "origin": "https://api.vendor.example" }`. This admits one exact HTTPS public DNS origin, with no wildcard, credentials, custom port, path, query or fragment. Approval is additive, bounded to 128 new origins, protected by the persistent integration keyring, and atomically stored as `operator-origins.protected` in the package directory before becoming effective. Approve the address, install the separately signed package, and refresh the clients without redeploying or restarting the host. Installation managers cannot perform approval, and manifests cannot approve their own destinations. Publisher trust remains deployment configuration. Preserve this protected file and its keys in backups; live removal of an existing approval is not supported.

The package store validates the archive digest, RSA-PSS publisher signature, every payload digest, paths, limits, protocol version, runtime, and network permissions before publishing its immutable artifact. Its atomic catalog survives host restarts. Installation does not enable a user connection; activation uses the installation's independently persisted revision and generation.

Workers communicate through bounded length-framed JSON-RPC over redirected standard streams. Credentials arrive only in the initialization frame. Setup tests and discovery use fresh ephemeral sessions; active connections keep their own session. Replacing a package or disabling an instance requires closing admission and stopping that instance through the coordinator; unresolved remote commands must remain in the durable command journal. A process sharing the host OS identity is not a sandbox: only trusted adapters are supported by this runtime.

Worker count, queued calls, frame size, deadlines and idle eviction are bounded by `IntegrationRuntime` options. `MaximumRememberedInstances` also bounds the generation fingerprints retained across worker eviction; reaching it rejects new instances without forgetting existing fences. Its default is 10,000 per process. Evicted records retain no credentials. Authoritative persisted revisions and generations restore fencing after a host restart.

A signed worker can request a longer operation deadline during initialization, up to `MaximumNegotiatedRequestTimeoutSeconds` (300 seconds by default). Caller cancellation remains authoritative. Shelly preserves request intervals from 1,000 to 120,000 milliseconds: discovery can require two throttled cloud calls, so its deadline reserves twice the interval plus two 30-second HTTP limits. A lower operator deadline reduces the admissible interval. Integration setup rejects intervals that exceed that bound.

The current provider APIs cannot attest a stable authoritative account identifier. Credential replacement therefore requires a new instance when identity cannot be established; matching device serials are insufficient evidence. Deye solar basis remains Unknown until the host's device-scoped measurement confirmation permits PV DC comparison. Socket reads retain unknown state/time when the remote response cannot prove them.

## Declarative setup layouts

`fields` remain the configuration schema. An optional `uiLayout` controls presentation only: version 1 contains bounded steps and groups, plain text instructions, references to declared field keys, and the action IDs `test`, `discover`, and `oauth`. A layout presents each field exactly once. `discover` uses the existing device selector and binding workflow; selected devices are not credential fields. Flat descriptors remain supported and retain their canonical descriptor digest when the optional members are absent.

```json
{
  "requiredUiFeatures": ["text", "secret", "wizard", "groups", "instructions", "conditional-fields", "device-selector"],
  "fields": [
    {"key":"account","kind":"text","label":"Account","required":true},
    {"key":"apiKey","kind":"secret","label":"API key","required":true,"activeWhen":{"field":"account","operator":"present"}}
  ],
  "actions": ["test", "discover"],
  "uiLayout": {"version":1,"steps":[
    {"id":"connection","title":"Connect","instructions":"Enter the account and its API key.","groups":[
      {"id":"credentials","title":"Credentials","fieldKeys":["account","apiKey"],"actions":["test"]},
      {"id":"devices","title":"Devices","instructions":"Choose devices after testing the connection.","fieldKeys":[],"actions":["discover"]}
    ]}
  ]}
}
```

Field and group `activeWhen` conditions are combined with AND. Conditions reference public fields only and support `eq`, `notEq`, `present`, and `absent`; circular dependencies and unknown references are rejected at installation. Missing keys use declared defaults, whereas explicit null overrides a default. Missing, null, and blank strings are absent; zero and false are present. `eq` and `notEq` both return false for absent operands. Strings compare exactly and number conditions use finite IEEE 754 equality to match JavaScript clients. An inactive field is retained, its required check is skipped, and any supplied value still must match its type and constraints. The shared test fixtures in `tests/shared/integration-ui-conditions.json` keep Web/server/mobile semantics aligned.

The package installer and packager call the shared BCL descriptor validator. Unsupported features, duplicate IDs, incorrect layout references, secret defaults, secret-dependent conditions, and excessive content are rejected before installation. Descriptor content never supplies HTML, executable code, arbitrary action URLs, or new native controls.

## Generic OAuth worker operations

A provider can declare `oauthDefinition: {"secretFieldKeys":["accessToken","refreshToken"]}` together with declared secret fields, the `oauth` action, and required UI feature `oauth`. The existing Deye and Shelly packages do not declare OAuth support. The test worker emulates the protocol without contacting a vendor or authorization service.

`oauth.begin` receives `{redirectUri,state,codeChallenge,codeChallengeMethod:"S256"}` and returns `{authorizationUrl}`. The runtime verifies an admitted HTTPS origin, exact state and callback URI, `response_type=code`, and unchanged S256 challenge. `oauth.complete` receives `{code,redirectUri,codeVerifier}` and returns `{success,publicValues,secretValues,accountIdentity?}`. Only schema-declared public fields and the OAuth secret whitelist are allowed in successful output; a rejected result returns no configuration or credentials. The host callback uses the operator's captured HTTPS `Auth:PublicBaseUrl` and the fixed `/integrations/oauth/callback` path, never a client or Host-header destination. Register that exact URI with the provider.

Both operations run in fresh bounded setup workers. They do not read or replace active credentials or token caches. The host owns the encrypted, expiring, user/installation/instance/revision-bound one-time flow, verifies callbacks and actor ownership, and atomically saves approved output. Providers must validate the authorization code and PKCE verifier at their real token service. There is no automatic retry of an uncertain exchange. Only the host returns public flow status to clients; OAuth secret output stays at the encrypted backend boundary.

Start with `POST /api/v2/integrations/{id}/oauth/start` and `{ "draft": <guarded configuration change>, "client": "web" }` or `"mobile"`. Start returns the flow ID, validated authorization URL, expiration and opaque return nonce. Flows expire after ten minutes. Read `/oauth/{flowId}` or cancel with `POST /oauth/{flowId}/cancel`; these operations require the owning manager. Web callbacks require the original account's authenticated cookie. Mobile callbacks validate the captured bearer session and return only `flowId` and `returnNonce` to the fixed `deyesolar://integration-oauth` URI. The mobile client checks both values and the current API session before reading status. Changing accounts, signing out, cancellation, security-stamp rotation and changed configuration generations invalidate the applicable flow.

Ready status supplies public draft values and credential-presence flags. Test and discovery can refer to the opaque `oauthFlowId` without exposing credentials or saving settings. Explicit configuration Save consumes the ready flow and appends the encrypted configuration in the same transaction; reuse is rejected. Editing another field invalidates the draft association. Cancellation cannot undo a provider grant already made remotely, but a late result cannot change local active configuration. Verified provider account identity must agree with an existing bound account and is retained on successful consumption.

The two device contract assemblies contain no provider implementation references. Inverter telemetry uses watts, percent, Celsius, volts and amperes; grid import and battery discharge are positive, while export and charging are negative. Every measurement carries its own source observation time and quality. Missing, invalid or older-than-ten-minute live observations cannot drive threshold rules or appear as a real zero. Observations without explicit valid quality are unavailable for automation. Provider adapters normalize their protocol at this boundary; rules and energy calculations consume device contracts only.

An asynchronous socket command must return its original command ID and a nonempty operation token, then support `socket.result` for that token. Pending commands cannot be released or replaced by a configuration, device selection or package change. Disable remains available and records their result as uncertain, without claiming remote cancellation. A lost result is never replayed automatically. Explicit release of an uncertain command requires an independent online observation, keeps the unknown result in the journal, and cannot prevent an earlier remote operation from finishing.

Back up SQL, installed package artifacts and both encryption keyrings before releases. Database recovery requires a matched image, schema, artifacts and keyrings from a coordinated backup; running EF migration `Down` is not a recovery procedure. Switching a compatible provider package to a retained earlier version is supported independently of database downgrade.

For Docker or Kubernetes, mount the package directory and encryption keyring on persistent storage or restore exact artifacts from an authenticated artifact store. No provider binaries are copied into the Web publish. Multi-host coordination is outside the existing single-replica deployment and requires shared leases/fencing before adding replicas.

For Docker Compose, set `SOLAR_INTEGRATION_DEPLOYMENT_DIRECTORY` to the generated deployment output and start with both `docker-compose.yml` and `docker-compose.integrations.yml`. The overlay requires the explicit bundle directory, mounts the public key and signed archives read-only, and persists both installed artifacts and the Data Protection keyring.

For Kubernetes, apply `k8s/integration-storage.yaml` before `k8s/deployment.yaml`. Populate the integration-bundle claim with the generated output's `integration-bundle` directory and sibling `integration-bootstrap.json` before starting the pod. The deployment mounts them read-only and keeps installed artifacts and encryption keys in separate writable claims. Never copy the publisher private key to a claim. Back up those writable claims together with SQL; deleting the keyring makes existing credentials unreadable. Keep one replica until shared command leases and process ownership fencing are implemented.
