# Solar generation estimates

Solar estimates describe available PV DC power for the configured installation, independently of measured Deye production. The dashboard and Generation detail view show a weather-based possible-power range alongside actual readings. Grid export, inverter AC output and PV DC generation are different quantities; sales calculations are documented separately in [solar-sales.md](solar-sales.md).

## Current weather and time

The current source is [Open-Meteo Forecast API](https://open-meteo.com/en/docs), using `models=best_match`. Each roof has its own request for `minutely_15=global_tilted_irradiance_instant,temperature_2m,wind_speed_10m,cloud_cover`, with UTC Unix timestamps and wind in m/s. The response is model output, not a sensor measurement at the panels. Fifteen-minute output does not establish native fifteen-minute model resolution; some data is interpolated from hourly values.

The server normally retrieves weather every ten minutes and evaluates the model every minute. Irradiance and available temperature, wind and cloud cover are linearly interpolated between adjacent points no more than fifteen minutes apart. There is no extrapolation beyond the returned timeline. Cloud cover already affects irradiance and is not applied again as a power multiplier.

Keep these times distinct:

- **Calculation time:** the instant for which power is evaluated.
- **Model bracket:** the forecast timestamps surrounding that instant.
- **RetrievedAt:** when the application obtained the weather response, not the weather model's initialization time.

Interpolation preserves the retrieval timestamp. It does not create a new cloud observation every minute. See [model updates](https://open-meteo.com/en/docs/model-updates) for provider update behavior.

One server-wide cache is persisted in `AppSettings`. A configuration fingerprint prevents weather for old roof geometry from being reused. By default, weather is marked stale after 20 minutes and is unusable after 30 minutes; stale weather or a failed refresh disables comparison status. The web tile also hides an evaluation that has stopped advancing for more than two minutes. Missing or invalid data is never converted to zero.

HTTP requests validate units, increasing timestamps, roof alignment and irradiance in the range 0–2000 W/m². Each request has a twelve-second timeout and at most three attempts for transient network failures, HTTP 429 or server errors. A long `Retry-After` postpones subsequent requests for that host; this cooldown is held in process memory.

The retained `OpenMeteoSolarClient` implements the earlier satellite source. Satellite results describe their past observation time and are not a fallback that can be relabeled as current weather. Satellite freshness settings remain separate from current-model settings.

## Installation configuration

`SolarEstimateOptions` and `src/DeyeSolar.Web/appsettings.json` define the packaged defaults. Environment variables use names such as `SolarEstimate__Roof1Kwp`; matching database `AppSettings` values take precedence. Check existing overrides before changing defaults. There is no dedicated solar-model settings form.

| Setting | Packaged value or assumption |
| --- | --- |
| Site | 50.095278, 20.070278; Geodetów 17E, Kraków |
| Display/calendar timezone | Europe/Warsaw |
| Roof 1, garden/southwest | 8 × 540 W = 4.32 kWp; tilt 25°; compass azimuth approximately 230° |
| Roof 2, road/northeast | 7 × 540 W = 3.78 kWp; tilt 25°; compass azimuth approximately 50° |
| Total STC capacity | 8.1 kWp |
| Module | LONGi LR7-60HVH-540M |
| Pmax temperature coefficient | −0.0026 /°C; coefficient spread 0 |
| DC losses | Central 6%; scenario bounds 3–12% |
| Faiman coefficients | U₀ = 25; U₁ = 6.84 |
| Wind at modules | 0.5 × modeled wind at 10 m |
| Additional cell heating | 3 °C × irradiance / 1000 |
| Cell-temperature allowance | ±12 °C |
| Configuration/weather-model allowances | 8% / 25% |

Compass azimuth is measured clockwise from true north. Open-Meteo uses south as zero, so the two API azimuths are +50° and −130°. The roof bearings are visual estimates from the supplied satellite screenshot, approximately ±10–15°, not surveyed bearings. The user's coordinates are retained; a nearby map centroid or inverter station coordinate is not substituted automatically.

The [official LONGi datasheet](https://eu.longi.com/longi-solar-panels-datasheets/lr7-60hvh-535-560) supplies the nominal Pmax coefficient. Setting its generic unknown-module spread to zero uses that nominal value; it does not certify zero physical uncertainty. Roof ventilation, local wind, shading and losses still require installation-specific evidence.

## Power model and range

Each roof is calculated independently, then the two powers are summed:

```text
v_module = wind_speed_10m × WindAtModuleFactor
Tcell = Tair + GTI / (FaimanU0 + FaimanU1 × v_module)
        + CellTemperatureRiseAt1000 × GTI / 1000
Pdc_kW = Pstc_kWp × GTI / 1000
         × max(0, 1 + TemperatureCoefficient × (Tcell − 25))
         × (1 − DcLossFraction)
```

GTI is irradiance on the tilted roof in W/m². Heating follows the [Faiman model](https://pvlib-python.readthedocs.io/en/stable/reference/generated/pvlib.temperature.faiman.html); power follows the [PVWatts DC temperature relationship](https://pvlib-python.readthedocs.io/en/stable/reference/generated/pvlib.pvsystem.pvwatts_dc.html). The baseline thermal coefficients are assumptions for this roof, not measured ventilation parameters.

The calculator combines the configured radiation allowance with configuration uncertainty, weather age and nearby irradiance variability. It evaluates extreme irradiance, cell-temperature, coefficient and loss scenarios, explicitly including the central result. The resulting lower–upper envelope is an engineering range, not a calibrated confidence interval or guaranteed output. It is not capped at the array's STC rating.

Missing temperature or wind uses explicit assumptions of 20 °C air and 1 m/s wind at the modules, adding another 20 °C to the temperature allowance. This broadens uncertainty instead of inventing a weather observation. Local shadows, snow and actual soiling are not directly measured. Inverter curtailment, battery limits and operating mode can reduce actual production; a deviation alone does not establish a fault. Actual readings are not used to calibrate either bound.

## Deye measurements and comparison

Comparison requires verified PV DC provenance for the selected inverter. Defaults are `DeyeSolarPowerIsPvDcConfirmed=false` and an empty `DeyeConfirmedDeviceSn`. Before enabling comparison, confirm that Deye `TotalSolarPower` represents the PV input sum in watts, then set the confirmation flag and bind it to the same serial as `DeyeCloud:DeviceSn`. Changing the device must not reuse another inverter's confirmation. Keep serials and credentials out of repository documentation.

Deye `collectionTime` is the measurement time, stored in UTC as nullable `Readings.SolarObservedAt`, with `SolarDeviceSn` identifying the source. `Readings.Timestamp` remains the polling time. Migration `20260918120000_AddSolarObservationTimestamp` adds these fields at startup; legacy rows are not assigned invented measurement times or backfilled automatically.

The current comparison separately evaluates the same forecast at the latest valid Deye measurement time, by default no more than ten minutes old. It does not compare an older measurement against a different current-time estimate. Future, stale, unconfirmed and foreign-device observations cannot establish comparison status. A configured `OperatingModeNote` is explanatory text, not a live inverter-mode signal. Near-zero generation does not produce a percentage comparison.

## Hourly generation history

The history chart displays **hourly mean power in kW**, with an amber possible-power band and a separate actual line. It does not display energy totals. Day, 7-day and 30-day windows end on the selected local date; selectable date anchors run from today back through the preceding 29 dates. Past days include their entire local day. Today stops at the latest completed hour. UTC storage and integration preserve distinct hours across 23/25-hour Warsaw daylight-saving days.

Historical possible power is reconstructed from available Open-Meteo Best Match weather with the **current** roof configuration. It is not a saved record of estimates previously shown. The hourly `global_tilted_irradiance` variable describes the preceding-hour mean, so the provider timestamp is shifted back one hour to identify the chart bucket. Temperature and wind describe the endpoint. Applying the thermal model to hourly means is an approximation. See the [historical forecast methodology](https://open-meteo.com/en/docs/historical-forecast-api).

A bounded shared history-weather cache lasts fifteen minutes; source failures are cached for two minutes. Geometry/date-window changes invalidate incompatible cache entries. Missing weather leaves a gap in the possible band while independently available actual readings remain visible. Real nighttime zero remains zero.

Actual history uses confirmed, device-specific measurement times. Duplicate polls at the same measured timestamp are deduplicated, retaining the latest valid persisted reading. Linear integration joins observations at most ten minutes apart, clips them to each hour and divides by covered duration. At least 90% of the hour must be covered. Missing actual coverage remains a gap. No extrapolation fills missing measurements.

Legacy `Readings` are retained for 31 days; rule-run logs remain limited to three days. Historical windows may extend beyond available actual readings, and previously deleted observations cannot be recovered. Export-sales storage has its own retention rules. An older application version may restore a shorter readings-retention policy, so assess that behavior before rollback.

## Operation and verification

Back up the database before upgrades and preserve existing configuration, credentials and volumes. Startup applies EF Core migrations. After changing geometry or device confirmation, verify the displayed parameters, source/retrieval times, fresh measurements and comparison state; a successful login alone does not establish valid generation data. For Compose deployments, update only the intended application service and preserve adjacent services. Do not automatically restore an old database when rolling back application code: newer observations could be lost.

```powershell
dotnet test DeyeSolar.sln --configuration Release --logger trx
dotnet publish src/DeyeSolar.Web --configuration Release
```

Set `SOLAR_TEST_SQL_CONNECTION` to an isolated test SQL Server to include maintained database tests; they create uniquely named temporary databases. Without it, SQL tests are skipped. The repository's `.github/workflows/tests.yml` provisions SQL Server 2022, runs the .NET suite and rejects missing/skipped test results. Workflow presence does not establish that a particular remote run passed or that merge protection is enabled.

Open-Meteo attribution and the derived-calculation label remain visible in the UI. Review the provider's [service terms](https://open-meteo.com/en/terms) for current usage limits and commercial-access requirements, and its [CC BY 4.0 licence](https://open-meteo.com/en/licence) for attribution. An optional `SolarEstimate:ApiKey` selects the customer API host; store it outside source control. Free-service availability is not guaranteed.
