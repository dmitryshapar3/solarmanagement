# Independently installed integration workers

The Web application references contracts and the runtime, not provider assemblies. Build and sign an adapter independently of the Web image. Only an operator can install executable packages. Never put the publisher private key in the application image, repository, package, or runtime configuration.

For a deployment that already has Deye or Shelly settings, prepare both built-in signed packages before upgrading the Web application. Generate the signing key once outside the repository and deployment output, then retain that publisher identity for subsequent releases:

```powershell
dotnet run --project tools/SolarManagement.IntegrationPackager -- --generate-key /secure/publisher-private-key.pem /secure/publisher-public-key.pem
./tools/Build-IntegrationDeployment.ps1 -SigningKeyPath /secure/publisher-private-key.pem -PublicKeyPath /secure/publisher-public-key.pem -OutputDirectory /build/solar-release -IncludeWeb
```

The output contains `web`, `integration-bundle` and an `integration-bootstrap.json` operator configuration file. Deploy the bundle read-only at `/app/integration-bundle`, the generated configuration alongside the Web application, durable writable artifacts at `/data/integration-packages`, and integration credential protection keys at `/data/keys/integrations`. Docker and Kubernetes mount `/data/keys` persistently and set `Integrations:KeyRingPath` to that integration subdirectory; the authentication keyring also uses the durable parent directory. Override `InstalledBundleDirectory`, `InstalledPackageDirectory` and `InstalledKeyRingDirectory` when preparing a Windows or different container deployment. The signing key is excluded. Trust configuration must come from the deployment file or environment, before editable SQL configuration is loaded.

At startup the configured archive digests and publisher signatures are verified and installed before importing legacy connections. Legacy import is installation-scoped and transactional: it encrypts the effective credentials, assigns stable internal device identities, maps rule targets and selected-source histories, preserves legacy label keys, and deletes old SQL secret rows only after commit can succeed. Missing packages leave the old settings and marker unchanged so installation can be retried. A completed migration suppresses legacy secret seeding. Setup testing is not required for this upgrade of an existing connection; changing credentials later follows the account identity rules below.

```powershell
dotnet publish integrations/SolarManagement.Providers.DeyeCloud -c Release -o ./artifacts/deye-worker
dotnet run --project tools/SolarManagement.IntegrationPackager -- ./artifacts/deye-worker ./integrations/manifests/deye.cloud.json /secure/publisher-private-key.pem ./artifacts/deye.cloud-1.0.0.zip
```

The final line is the exact SHA256 authorized by the installation request. Configure `IntegrationRuntime:PackageDirectory` on durable storage, the publisher PEM public key under `TrustedPublisherPublicKeys:solar-management`, and the operator-approved origins separately. Deye uses its EU/US HTTPS origins; Shelly uses `https://*.shelly.cloud`. The signed manifest cannot grant origins absent from host policy. Add no production credential to package templates.

For ongoing releases, give each package an immutable new version and use the operator package installation endpoint with its exact SHA256. Keep previous artifacts and the encryption keyring when backing up or restoring; persisted instances require their pinned package digest. Built-in bootstrap archives can remain configured because repeated installation verifies and reuses the same immutable version. Do not replace a signed archive in place under an existing version, or rotate the trusted publisher key without retaining the prior public key under its prior publisher identifier.

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

The package store validates the archive digest, RSA-PSS publisher signature, every payload digest, paths, limits, protocol version, runtime, and network permissions before publishing its immutable artifact. Its atomic catalog survives host restarts. Installation does not enable a user connection; activation uses the installation's independently persisted revision and generation.

Workers communicate through bounded length-framed JSON-RPC over redirected standard streams. Credentials arrive only in the initialization frame. Setup tests and discovery use fresh ephemeral sessions; active connections keep their own session. Replacing a package or disabling an instance requires closing admission and stopping that instance through the coordinator; unresolved remote commands must remain in the durable command journal. A process sharing the host OS identity is not a sandbox: only trusted adapters are supported by this runtime.

Worker count, queued calls, frame size, deadlines and idle eviction are bounded by `IntegrationRuntime` options. `MaximumRememberedInstances` also bounds the generation fingerprints retained across worker eviction; reaching it rejects new instances without forgetting existing fences. Its default is 10,000 per process. Evicted records retain no credentials. Authoritative persisted revisions and generations restore fencing after a host restart.

A signed worker can request a longer operation deadline during initialization, up to `MaximumNegotiatedRequestTimeoutSeconds` (300 seconds by default). Caller cancellation remains authoritative. Shelly preserves request intervals from 1,000 to 120,000 milliseconds: discovery can require two throttled cloud calls, so its deadline reserves twice the interval plus two 30-second HTTP limits. A lower operator deadline reduces the admissible interval. Legacy cutover refuses the entire import when an interval exceeds that bound, preserving the original settings and secrets for correction; it never clamps a large explicit interval or writes the completion marker.

The current provider APIs cannot attest a stable authoritative account identifier. Credential replacement therefore requires a new instance when identity cannot be established; matching device serials are insufficient evidence. Deye solar basis remains Unknown until the host's device-scoped measurement confirmation permits PV DC comparison. Socket reads retain unknown state/time when the remote response cannot prove them.

The two device contract assemblies contain no provider implementation references. Inverter telemetry uses watts, percent, Celsius, volts and amperes; grid import and battery discharge are positive, while export and charging are negative. Every measurement carries its own source observation time and quality. Missing, invalid or older-than-ten-minute live observations cannot drive threshold rules or appear as a real zero. Legacy historical records without quality retain their existing meaning. Provider adapters normalize their protocol at this boundary; rules and energy calculations consume device contracts only.

An asynchronous socket command must return its original command ID and a nonempty operation token, then support `socket.result` for that token. Pending commands cannot be released or replaced by a configuration, device selection or package change. Disable remains available and records their result as uncertain, without claiming remote cancellation. A lost result is never replayed automatically. Explicit release of an uncertain command requires an independent online observation, keeps the unknown result in the journal, and cannot prevent an earlier remote operation from finishing.

Back up SQL, installed package artifacts and both encryption keyrings before the first upgrade. A database/application downgrade after encrypted credential import requires restoring that consistent pre-upgrade backup. Running EF migration `Down` alone removes the new connection tables and cannot reconstruct the deleted legacy plaintext credentials. Switching a compatible provider package to a retained earlier version is supported independently of database downgrade.

For Docker or Kubernetes, mount the package directory and encryption keyring on persistent storage or restore exact artifacts from an authenticated artifact store. No provider binaries are copied into the Web publish. Multi-host coordination is outside the existing single-replica deployment and requires shared leases/fencing before adding replicas.

For Docker Compose, set `SOLAR_INTEGRATION_DEPLOYMENT_DIRECTORY` to the generated deployment output and start with both `docker-compose.yml` and `docker-compose.integrations.yml`. The overlay requires the explicit bundle directory, mounts the public key and signed archives read-only, and persists both installed artifacts and the Data Protection keyring.

For Kubernetes, apply `k8s/integration-storage.yaml` before `k8s/deployment.yaml`. Populate the integration-bundle claim with the generated output's `integration-bundle` directory and sibling `integration-bootstrap.json` before starting the pod. The deployment mounts them read-only and keeps installed artifacts and encryption keys in separate writable claims. Never copy the publisher private key to a claim. Back up those writable claims together with SQL; deleting the keyring makes existing credentials unreadable. Keep one replica until shared command leases and process ownership fencing are implemented. After a successful legacy import, remove legacy provider credentials from deployment secrets/environment as part of the operator cutover.
