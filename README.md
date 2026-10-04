# SolarManagement (DeyeSolar)

DeyeSolar monitors a Deye solar inverter, battery and grid connection, and controls Shelly sockets using configurable automation rules. It includes an authenticated web dashboard and an Expo mobile client for Android and iOS.

The dashboard separates measured energy data, modeled solar generation and estimated electricity export value. Export estimates are based on inverter observations and Polish PSE RCE prices; they are not invoice reconciliation or a statement of payment due.

## Features

- Live battery state of charge, solar production, battery power, grid exchange and household load from DeyeCloud.
- Shelly device discovery, manual switching and rules with battery, solar-production, schedule, cooldown and evaluation-interval settings.
- Historical readings and generation charts, with weather-based possible-power ranges from Open-Meteo.
- Electricity export estimates with retained observations and quarter-hour prices, coverage indicators and separate provisional current-hour progress.
- Verified email/phone registration, Google sign-in, linked identities, isolated installations and an authenticated API for the mobile client.
- User-selectable interface language with 15 shared offline translations on web and mobile, including localized dates and numbers.

## Project layout

| Directory | Purpose |
| --- | --- |
| `src/DeyeSolar.Domain` | Models, integration contracts, typed options and calculation helpers |
| `src/DeyeSolar.Infrastructure` | DeyeCloud, Shelly, Open-Meteo and PSE clients |
| `src/DeyeSolar.RuleEngine` | Socket automation rule evaluation |
| `src/DeyeSolar.Web` | ASP.NET Core 8, Blazor Server, MudBlazor, API, background workers and EF Core migrations |
| `mobile` | Expo / React Native / TypeScript mobile application |
| `tests` | Rule-engine, infrastructure and web tests |
| `docs` | Solar-model and electricity-export behavior documentation |
| `k8s` | Kubernetes manifests and deployment script |

## Run the backend locally

Install the .NET 8 SDK and provide a reachable SQL Server instance. The application uses SQL Server through Entity Framework Core; it applies migrations and seeds initial settings when it starts.

From the repository root, set a connection string for your own development database and start the backend:

```powershell
$env:ConnectionStrings__DefaultConnection = 'Server=localhost,1433;Database=DeyeSolarDev;User ID=sa;Password=<your-development-password>;Encrypt=True;TrustServerCertificate=True;MultipleActiveResultSets=true'
dotnet restore DeyeSolar.sln
dotnet run --project src/DeyeSolar.Web --no-launch-profile --urls http://localhost:5000
```

Open `http://localhost:5000`. For an optional bootstrap administrator on a fresh database, set `Auth__BootstrapAdminPassword` to a password of at least 12 characters. Provisioning creates the account and a unique empty installation atomically. Verified public registration is enabled when a delivery provider or Google is configured. Configure providers through the signed-package integration setup in Settings, then select devices and create automation rules.

The tracked configuration includes development database defaults and installation-specific solar-model defaults. Replace the database connection and review the solar configuration for your installation before use. Keep credentials in deployment environment variables or database settings rather than committing them to source control.

## Configuration

| Section | Main settings |
| --- | --- |
| `Auth` | Public HTTPS origin, registration switch, Google OAuth, Resend email, Twilio Verify SMS and optional bootstrap password |
| `ConnectionStrings` | `DefaultConnection` for SQL Server |
| `Polling` | Inverter polling interval |
| `Display` | User-facing timezone |
| `SolarEstimate` | Site coordinates, roof capacities/orientations, temperature/loss model, freshness limits and server-only Open-Meteo `ApiKey` |
| `SolarSales` | Contract start date, contract timezone and treatment of negative prices |

Operator environment variables use double underscores, for example `SolarEstimate__ApiKey` or `Auth__PublicBaseUrl`. Authentication, billing, publisher trust and weather credentials come only from operator configuration. Provider credentials use the encrypted dynamic integration configuration; there are no deployment Deye/Shelly defaults or imports. Each installation starts with neutral polling, display, solar-site and sales-contract settings and saves those settings in its own SQL rows. Editable SQL cannot override operator credentials.

Commercial app launch requires valid commercial Open-Meteo access. A missing weather API key retains the existing non-commercial evaluation service. See [Open-Meteo commercial API setup](docs/open-meteo-commercial.md) before deploying a paid product.

Configure the installation timezone and contract start date to match the applicable agreement before interpreting sales results. Possible-power estimates also depend on the configured roof geometry and weather model, and are not guaranteed production ranges.

## Run the mobile app

```powershell
cd mobile
npm ci
npm run typecheck
npm start
```

The default backend address is `https://solar.dshapar.com`. Sign in with the existing Solar account, or enter a custom backend URL on the login screen.

The client signs in with `POST /api/auth/login` and uses bearer tokens for the remaining API calls. On a Mac, run `bash mobile/prepare-xcode.command` to prepare and open the iOS workspace. See [mobile/README.md](mobile/README.md) for Xcode signing and TestFlight instructions; no Expo account is required for this workflow.

## Build and test

```powershell
dotnet build DeyeSolar.sln --configuration Release
dotnet test DeyeSolar.sln --configuration Release --logger trx
```

SQL-backed web tests require `SOLAR_TEST_SQL_CONNECTION` to point to an isolated SQL Server instance. The tests create temporary databases, so the test login needs permission to create and drop them. Without this variable, those tests are skipped; a passing unit-test run alone does not verify database behavior.

The GitHub Actions workflow in `.github/workflows/tests.yml` provisions SQL Server 2022, runs every .NET test project, and checks that tests were discovered and none were skipped. Its mobile job installs locked dependencies, checks TypeScript, runs permanent regression tests and exports the iOS JavaScript bundle.

## Containers and deployment

The `Dockerfile` publishes the backend using .NET 8 and serves HTTP on port 8080. `docker-compose.yml` provisions SQL Server, a privileged migration job and the restricted application runtime. Generate protected secrets with `scripts/prepare-compose.py`, build the signed provider bundle, and use `docker-compose.integrations.yml` as described in the production operations guide.

Production startup validates the migrated schema and uses a restricted SQL login. Run the privileged migration job before starting the runtime. Compose provisions isolated SQL, persistent key/package storage and file-based connection secrets; Kubernetes uses a separate migration job and one automation owner. See [production releases and recovery](docs/production-operations.md) for setup, health probes, coordinated encrypted backup/restore and executable Docker smoke checks. Building the iOS app does not require deploying the backend again.

## More detail

- [Accounts, delivery providers and deployment](docs/accounts-deployment.md)
- [Solar generation model and history](docs/solar-expected-power.md)
- [Open-Meteo commercial API setup](docs/open-meteo-commercial.md)
- [Electricity export estimates, storage and upgrades](docs/solar-sales.md)
- [Public backend pages and App Store release checks](docs/app-store-backend.md)

The [production readiness and SOLID audit (5 October 2026)](docs/production-readiness-audit-2026-10-05.md) records implemented safeguards, validation evidence and remaining rollout conditions.
