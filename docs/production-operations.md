# Production releases and recovery

The backend keeps one automation owner. Logging infrastructure and horizontal scaling are outside this release. Trial and Apple subscription policy is unchanged: migrations preserve existing cutover dates and account tokens; restarts never start a new trial. Expired access remains closed when Apple cannot refresh its verified cache.

## Startup and database privileges

`Program` composes services, initializes the release, and starts HTTP. `DeploymentConfiguration` captures operator authentication, billing and publisher trust before editable SQL settings. `ApplicationDatabaseInitializer` verifies signed bootstrap artifacts before modifying the database. `ApplicationSeedData` atomically provisions an optional administrator with its own new installation and membership. Installations receive neutral site defaults; provider credentials are configured explicitly through integration setup. There is no shared legacy installation or automatic provider import.

Production HTTP requires `Operations__DatabaseMode=validate`; it cannot enable migrations or disable runtime privilege checks through configuration. It refuses pending or unknown migrations, missing mapped tables/columns, unavailable pinned artifacts, and a runtime login with administrative or schema modification privileges. Development defaults to `migrate`. An explicit `dotnet DeyeSolar.Web.dll --migrate-only` runs signed-package preflight, migrations and bootstrap, optionally provisions the DML-only runtime principal, then exits without starting HTTP or workers. On an existing database, it also checks migration compatibility and all pinned package versions before modifying the schema. Use a separate privileged connection for that job. Migration failure leaves the application stopped; do not automatically downgrade or reset billing records.

Connection strings and bootstrap passwords support file-based secrets: `ConnectionStrings__DefaultConnectionFile` and `Auth__BootstrapAdminPasswordFile`. `Operations__RuntimeDatabaseUser` and `Operations__RuntimeDatabasePasswordFile` provision an SQL login with SELECT/INSERT/UPDATE/DELETE on the application schema. This job also rebinds the restored database user to its server login. Authentication/billing provider keys remain operator secrets. Keep secret files and signing keys outside the repository.

## Compose

Generate a new protected directory on the host:

```sh
python3 scripts/prepare-compose.py /secure/solar-deployment
```

Build the signed integration release using `tools/Build-IntegrationDeployment.ps1` and the established publisher key. Make its public bundle readable by container UID 1654, and set `SOLAR_INTEGRATION_DEPLOYMENT_DIRECTORY` to that release directory. Start with both Compose files:

```sh
docker compose --env-file /secure/solar-deployment/compose.env \
  -f docker-compose.yml -f docker-compose.integrations.yml up --build -d
```

SQL starts first, the migration job must finish successfully, and then the non-root runtime starts with its own restricted connection. SQL, package catalog/artifacts and protection keys use durable named volumes. The host HTTP binding is localhost; terminate HTTPS at the reverse proxy and configure its actual trusted IP addresses. That proxy must overwrite X-Forwarded-For and X-Forwarded-Proto; only explicitly trusted peers can change the client IP and HTTPS scheme. Review SQL edition and storage capacity for the deployment; the smoke test uses Developer edition only for tests. Keep the bootstrap administrator password private and replace initial access through the account controls.

For an update, stop `deye-solar` before running the migration job so schema changes and automation owners cannot overlap. Take the coordinated backup below first. Recreate the migration service for each release, then start the runtime. Compose `depends_on` protects initial ordering; it does not replace this update cutover.

The first transition from older root images also requires transferring application volume ownership to UID/GID1654. Restore performs this transfer automatically. When retaining existing volumes directly, back them up, stop every writer, and run a one-off root helper to chown only the application key/package volumes before the new image starts. Preserve all existing encryption key files and their directory layout; never substitute newly generated keys. The SQL data volume retains SQL Server ownership. The older accounts guide's `/root/.aspnet` path must become an accessible mount in the new non-root deployment.

## Health and Kubernetes

`/health/live` reports HTTP process liveness independently of SQL, billing, sessions or installations. `/health/ready` checks schema/SQL with a bounded deadline, writable durable storage, and registered internal worker heartbeats. Responses contain only aggregate status and use no-store. External provider failures remain tenant/billing degradation and do not force liveness restarts.

Kubernetes uses `Recreate`, a startup probe and separate readiness/liveness paths. Provision the `deye-solar-database` Secret with `migration-connection`, `runtime-connection` and `runtime-password`, plus `bootstrap-password` when creating an initial administrator; populate the signed integration-bundle PVC before deploying. Run `k8s/deploy.sh` with explicit `KUBE_CONTEXT` and optional `IMAGE_TAG`. It builds/pushes the requested release, resolves its immutable image digest, stops the old owner, runs the migration Job, then deploys that exact digest. On migration failure it leaves automation stopped for deliberate recovery.

## Consistent backup and fresh-target restore

`scripts/production-state.py` stops the application, rejects other local containers writing its application volumes, runs SQL COPY_ONLY backup with CHECKSUM/VERIFYONLY, then snapshots ordered key and package volumes. It includes optional signed bundle/bootstrap configuration and checksums. SQL/schema, billing trial dates, account tokens and subscription rows get a consistency fingerprint. It restarts the original application in a finally block.

Create a long backup passphrase in a protected file stored separately from the backups. The archive is encrypted with AES-256-CBC/PBKDF2 and authenticated with a separately derived HMAC-SHA256 key; authentication is checked before any target change. Keep off-host copies, the passphrase and deployment/provider secrets in appropriate protected stores. Select an actual backup cadence and retention period for the application's recovery objective.

```sh
python3 scripts/production-state.py backup --app solar-app --sql solar-sql --database DeyeSolar \
  --volume solar_protection-keys --volume solar_integration-packages \
  --bundle /releases/current/integration-bundle --bootstrap-config /releases/current/integration-bootstrap.json \
  --output /backups/solar-20261004 --passphrase-file /secure/backup-passphrase

python3 scripts/production-state.py restore --sql fresh-sql --image solar-management:retained-release \
  --volume restored-keys --volume restored-packages --backup /backups/solar-20261004 \
  --bundle-output /releases/restored/integration-bundle --bootstrap-output /releases/restored/integration-bootstrap.json \
  --passphrase-file /secure/backup-passphrase
```

Restore requires an absent target database and empty inactive application volumes; it never replaces an existing production database. The current helper supports one SQL data file and one log file. It verifies trial/token/subscription fingerprints, restores keyrings and artifacts, and assigns application volume ownership to the target non-root UID/GID (1654 by default). Mount the restored release, run its privileged migration/provisioning job to rebind the restricted SQL user, then start that retained version. Restore Apple signing credentials and trust files from the protected operator store; do not generate replacement account tokens or execute migration Down against live billing records.

RPO equals the age of the latest verified snapshot; RTO includes SQL restore, artifact/key restoration, credential provisioning and application checks. Set concrete targets and repeat the drill on representative data. The repository does not claim production-sized RPO/RTO from a small fixture.

Expired account sessions and integration authorization flows have indexed, bounded background cleanup. Command deduplication records, configuration history and encryption keys remain retained: deleting a terminal command can permit the same command identifier to dispatch again, and deleting its configuration or key can break receipt recovery. Define storage budgets and a tombstone/receipt/key retention policy before pruning that history. Retain each published signed archive exactly as released; rebuilding the same package version can produce a different archive digest and must not replace an immutable artifact.

## Executable release smoke

Every .NET project has a committed `packages.lock.json`. CI and the production image restore in locked mode; update locks deliberately when changing dependencies. `scripts/audit-nuget.py` rejects known vulnerabilities in both direct and transitive dependencies. Native StoreKit, browser and provider suites remain required alongside the container drill.

```sh
python3 scripts/production-smoke.py
```

This builds the actual Production image, signs all provider packages with an ephemeral fixture publisher, runs fresh Docker SQL, rejects a bad bundle before schema creation and an administrative runtime login, checks actual login/trial/provider API, persists sessions across restart, verifies readiness failure within its deadline during SQL outage while liveness stays healthy, takes an authenticated encrypted coordinated backup, restores fresh SQL/volumes and verifies preserved billing/token/key/artifact state. Fresh provisioning creates an independent administrator installation, without a shared ownership default or automatic provider import. The drill verifies that repeated migration preserves its trial and exact signed package pin, and that unknown or removed API paths return JSON 404 for both anonymous and authenticated requests. Provider protocol compatibility has its own required suite. The smoke never calls a live provider or performs a purchase. Existing browser billing, provider protocol and native StoreKit suites remain required separately. `--keep` retains isolated fixtures/private diagnostics for debugging; default cleanup touches only resources created by that smoke run.
