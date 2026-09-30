# SolarManagement (DeyeSolar)

DeyeSolar monitors a Deye solar inverter, battery and grid connection, and controls Shelly sockets using configurable automation rules. It includes an authenticated web dashboard and an Expo mobile client for Android and iOS.

The dashboard separates measured energy data, modeled solar generation and estimated electricity export value. Export estimates are based on inverter observations and Polish PSE RCE prices; they are not invoice reconciliation or a statement of payment due.

## Features

- Live battery state of charge, solar production, battery power, grid exchange and household load from DeyeCloud.
- Shelly device discovery, manual switching and rules with battery, solar-production, schedule, cooldown and evaluation-interval settings.
- Historical readings and generation charts, with weather-based possible-power ranges from Open-Meteo.
- Electricity export estimates with retained observations and quarter-hour prices, coverage indicators and separate provisional current-hour progress.
- ASP.NET Core Identity login, database-backed application settings and an authenticated API for the mobile client.

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

Open `http://localhost:5000`. On a fresh database, startup creates the `admin` account and prints its generated password to the backend console. Configure DeyeCloud and Shelly access through the Settings page after signing in. The initial automation rule is disabled until a device and the intended conditions are configured.

The tracked configuration includes development database defaults and installation-specific solar-model defaults. Replace the database connection and review the solar configuration for your installation before use. Keep credentials in deployment environment variables or database settings rather than committing them to source control.

## Configuration

| Section | Main settings |
| --- | --- |
| `ConnectionStrings` | `DefaultConnection` for SQL Server |
| `DeyeCloud` | Regional API URL, application ID/secret, account email/password, station ID and inverter serial number |
| `Shelly` | Cloud server URI, authentication key and configured device ID |
| `Polling` | Inverter polling interval |
| `Display` | User-facing timezone |
| `SolarEstimate` | Site coordinates, roof capacities/orientations, temperature/loss model, freshness limits and server-only Open-Meteo `ApiKey` |
| `SolarSales` | Contract start date, contract timezone and treatment of negative prices |

Environment variables use double underscores, for example `DeyeCloud__AppId` or `SolarEstimate__Roof1Kwp`. Matching values already stored in the SQL `AppSettings` table take precedence over application files and environment variables, except the Open-Meteo key: `SolarEstimate__ApiKey` is read only from server configuration before SQL settings are loaded. The Settings page manages DeyeCloud, Shelly, polling and display settings; the solar estimate and sales options are configuration sections without dedicated edit forms.

Commercial app launch requires valid commercial Open-Meteo access. A missing weather API key retains the existing non-commercial evaluation service. See [Open-Meteo commercial API setup](docs/open-meteo-commercial.md) before deploying a paid product.

The packaged solar-sales policy uses the Warsaw timezone and a specific contract start date. Adapt it to the applicable agreement before interpreting results. Possible-power estimates also depend on the configured roof geometry and weather model, and are not guaranteed production ranges.

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

The `Dockerfile` publishes the backend using .NET 8 and serves HTTP on port 8080. `docker-compose.yml` runs the application against an external SQL Server; it does not provision a database. Update its connection configuration for the target environment before running `docker compose up --build`.

Legacy Kubernetes manifests and deployment tooling are under `k8s`. Review the target cluster, database connection and required secrets before using them. Back up the database before deploying a version with new migrations. Building the iOS app does not require deploying the backend again.

## More detail

- [Solar generation model and history](docs/solar-expected-power.md)
- [Open-Meteo commercial API setup](docs/open-meteo-commercial.md)
- [Electricity export estimates, storage and upgrades](docs/solar-sales.md)
- [Public backend pages and App Store release checks](docs/app-store-backend.md)
