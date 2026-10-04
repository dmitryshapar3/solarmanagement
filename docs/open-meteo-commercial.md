# Open-Meteo commercial API configuration

The current release plan is **TestFlight evaluation using the existing free API**, with public App Store release deferred. Leave the key unset for that testing; these optional backend changes do not require a redeployment to finish the current TestFlight build.

A later commercial release needs valid commercial Open-Meteo access. A StoreKit subscription purchased by an app user does not pay for the backend's weather provider. Open-Meteo's free API is for non-commercial evaluation/prototyping; leaving the key unset preserves that development behavior and does **not** authorize a commercial release. See the provider's [pricing and commercial licence](https://open-meteo.com/en/pricing) and [service terms](https://open-meteo.com/en/terms).

## Actual requests

| Backend source | Evaluation endpoint without a key | Customer endpoint with a key |
| --- | --- | --- |
| Current Best Match solar weather, two roofs | `https://api.open-meteo.com/v1/forecast` | `https://customer-api.open-meteo.com/v1/forecast` |
| Recent hourly weather for the solar-history chart, two roofs | `https://api.open-meteo.com/v1/forecast` with `start_date` / `end_date` | `https://customer-api.open-meteo.com/v1/forecast` with the same date range |
| Retained satellite radiation source, two roofs | `https://satellite-api.open-meteo.com/v1/archive` | `https://customer-satellite-api.open-meteo.com/v1/archive` |

The currently registered sources are `OpenMeteoCurrentSolarClient` and `OpenMeteoSolarHistoryClient`. The retained `OpenMeteoSolarClient` also requests temperature/wind from the Forecast API when used, and receives the same customer routing protection. This change does not activate that satellite source or alter the calculation model.

The history chart currently uses the Forecast API's recent past-date support, not the separate Historical Forecast API or ERA5 Historical Weather API. It keeps the existing 30-day chart behavior. The separate historical and satellite APIs require **Professional or higher** under the current pricing table; Standard does not include those APIs. Choose Professional or higher for satellite/full historical coverage, or confirm with Open-Meteo that a narrower plan covers all intended production requests. Do not assume that a key by itself grants access to every dataset.

Official endpoint/parameter references: [Forecast documentation](https://open-meteo.com/en/docs), [Historical Forecast documentation](https://open-meteo.com/en/docs/historical-forecast-api), [Satellite Radiation documentation](https://open-meteo.com/en/docs/satellite-radiation-api), and the provider's [endpoint host routing source](https://github.com/open-meteo/open-meteo/blob/main/Sources/App/Controllers/ForecastapiController.swift).

## Set the server secret

Obtain the API subscription and key directly from Open-Meteo. This repository does not purchase a plan, create an account/key, or deploy the backend.

Set **`SolarEstimate__ApiKey`** in the backend deployment's secret environment configuration. The equivalent ASP.NET Core key is `SolarEstimate:ApiKey`. For local evaluation of a customer subscription, use .NET user secrets or an untracked private deployment configuration. Never add the real key to tracked `appsettings.json`, a mobile `.env`, an API request/DTO, App Store metadata, a support message, or a screenshot.

For the existing Compose service, add this entry under its `environment` list and provide the value through the deployment environment/secret manager:

```yaml
- SolarEstimate__ApiKey=${SolarEstimate__ApiKey:?Set the Open-Meteo server key securely}
```

Do not print `docker compose config` or dump the container environment after adding the secret; those commands can expose its value. Kubernetes deployments should reference an existing Secret for this same environment variable. Changing a host environment variable alone does not update an existing process/container: redeploy/recreate the intended backend service using the established deployment process.

The backend captures the key from server configuration before loading SQL `AppSettings`; a database setting cannot override it. It trims surrounding whitespace, appends the URL-escaped `apikey` parameter, and uses fixed HTTPS customer hosts. A missing/blank key uses only the existing evaluation hosts. Customer authentication failure never causes fallback to the free service.

All three provider clients disable automatic redirects and HttpClient URL logging. Transport, malformed-response and cancellation errors are sanitized without retaining the URL, response body, or inner exception. Keep external proxy/APM/network tracing configured to redact `apikey`; deliberately enabling request-URL tracing outside these clients could still record a provider URL.

## Build, deploy and verify

```sh
dotnet test tests/DeyeSolar.Infrastructure.Tests/DeyeSolar.Infrastructure.Tests.csproj -c Release --filter FullyQualifiedName~OpenMeteo
dotnet build src/DeyeSolar.Web/DeyeSolar.Web.csproj -c Release
```

Deploy the backend containing these changes after configuring the secret. No migration or mobile release is required. The existing application startup still requires its SQL Server connection and applies any previously pending migrations; preserve the production database and other secrets.

After deployment, sign in normally and check that the current solar estimate has a new successful retrieval time, plus valid history weather/possible-power data for Today and a previous date. Existing caches can temporarily show previously fetched data, so an HTTP 200 or an old estimate alone is insufficient. If requests fail, inspect the sanitized provider error/status and Open-Meteo subscription access; do not paste a full customer URL containing the key into logs or support tickets. Verify the provider's customer portal/account confirms active commercial access. Real paid-key behavior must be verified after the operator installs the actual key; fake-handler unit tests establish routing and secret handling only.

Keep the existing Open-Meteo attribution and derived-calculation labels visible, including with paid access. Subscription access does not remove the provider's attribution requirement. Recheck provider plans/terms when changing datasets, request volume or locations.
