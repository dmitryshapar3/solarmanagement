# Electricity export estimates

The sales dashboard estimates electricity export value from the selected Deye inverter. The configured TAURON contract starts on **2026-09-28**, using **Europe/Warsaw** calendar dates. Day, month, year and custom periods contain only completed, eligible hours in their totals. Custom periods are limited to 366 calendar days; Warsaw daylight-saving days retain their distinct 23 or 25 UTC hours.

These are Deye-based estimates. The distribution system operator's settlement meter, supplier adjustments and invoices remain separate sources. The dashboard does not reconcile invoices or report a cash balance or payment due.

## Energy and value

Deye `TotalGridPower` is signed watts: **negative means export; positive means import**. This is grid exchange, not PV generation. The calculation linearly interpolates between actual measurement timestamps, splits sign crossings and integrates the positive and negative areas separately.

An hour enters completed totals only when measurements cover the entire hour with gaps of **at most 10 minutes**. Boundary observations may come from up to ten minutes before or after the hour, but only energy inside the hour contributes. There is no extrapolation or filling of missing periods with zero. The unfinished hour is shown separately as provisional progress.

For each complete hour:

```text
credited export kWh = max(0, gross export kWh - import kWh)
eligible quarter price = max(0, published RCE price in PLN/MWh)
hour value PLN = credited export kWh / 4 × sum(four eligible quarter prices) / 1000
estimated deposit PLN = value PLN × 1.23
```

The contract allocates the net credited hourly export equally to the four quarter-hour prices. It does not price gross exported energy independently of imports. The 1.23 multiplier estimates the deposit credit under the supplied contract; it is not a cash payout. Period totals sum the hourly values without intermediate currency rounding; formatting rounds the displayed amounts.

All four quarter prices are required for an hour with positive credited export. A fully observed hour with no credited export has known zero value even when prices are missing. A missing measurement or required price remains unknown. Totals include available complete hours, with separate expected, observed and valued hour counts; partial totals must not be read as full-period results.

## Current-hour progress

When the selected calendar period includes the active contract's current hour, the response also includes `CurrentHour`. This applies to current day, month, year and custom periods. Historical periods have no current-hour progress. The first contract hour can show progress before any completed hours exist; at an exact hour boundary the new hour has no progress yet.

Progress covers the current UTC hour's start through the latest actual Deye observation at or before the request snapshot. The entire observed span must be covered with the same ten-minute gap limit. Future samples are excluded, and the latest sample is not extended to the wall clock. `ObservedThrough` is the measurement cutoff; `UpdatedAt` is the request's captured time. An older but valid observation may remain visible with its truthful cutoff while a backfill is attempted. Missing coverage leaves the progress values and cutoff unknown.

Current-hour export, net credited export, value and estimated deposit remain separate from completed totals and chart buckets. Later imports within the same hour can reduce its provisional net credit and value. Positive current credit still requires all four hourly quarter prices, including quarters later than the observation; incomplete price publication leaves money unknown. The period totals accept that hour only after it closes and its full measurement coverage is available.

The dashboard presents **Generation** and **Sales** as separate views: PV production and possible generation are different from exported electricity and its estimated value. **Details** opens the corresponding detailed view. The sales view supports manual refresh and refreshes every five minutes while mounted. This reads newly stored observations; it does not create new measurements or turn provisional amounts into final settlement values.

## Sources and durable storage

Live ingestion uses Deye `/device/latest`; backfill uses `/device/historyRaw` for `TotalGridPower`. Grid provenance requires a valid device identity, measurement time and explicit `W` unit. The timestamp is the device observation time, not the application polling time. PV validity does not establish grid validity. Invalid grid provenance cannot create an export observation.

`ExportReadings` stores signed integer watts by the case-sensitive composite key `(DeviceSn, ObservedAt)`, with `PolledAt` recording when the observation was obtained. Duplicate polls do not multiply energy. A later obtained correction can replace the same observation; an older retry cannot overwrite it. Live grid observations and the corresponding legacy `Reading` are saved in one transaction. Backfill batches are also atomic, and device identities remain isolated.

Prices come from the official [PSE RCE publication](https://raporty.pse.pl/report/rce-pln), through its `rce-pln` API. `dtime_utc` is interpreted as the exclusive end of a 15-minute interval. `ExportPrices` stores each interval's UTC start, signed `decimal(18,6)` PLN/MWh price and retrieval time. Prices are shared market data rather than device-specific records. Negative published prices are retained unchanged; the contract floor is applied during calculation.

Neither export table is subject to the legacy `Readings` 31-day cleanup. They retain observations and prices for longer-term statistics. Existing legacy grid rows are **not** automatically imported: their polling timestamps and values lack the independent grid provenance needed for this calculation. Backfill depends on the history actually available from Deye; missing cloud history stays missing.

## Loading and refresh

Opening or refreshing a period first reads durable observations and prices. Missing measurement coverage, or current-hour progress older than ten minutes, can trigger Deye backfill. Requests use windows of at most six hours, capped at 14 requests per service call. A current-hour window needing refresh has priority, followed by historical windows not yet attempted, so long periods do not repeatedly consume the limit on the same oldest gaps. Each attempted device/window is throttled for five minutes after success or two minutes after failure. Large or incomplete periods can therefore require several refreshes.

PSE is queried for unvalued hours with positive credited export, with a two-minute retry throttle. Partial responses preserve already stored prices. Complete cached price intervals are reused without a scheduled reconciliation against later PSE revisions; this is a durable calculation cache, not an invoice reconciliation journal. A source or storage failure leaves unavailable values unknown while retaining usable stored data. The service fences configuration or device changes before accepting external results, and cancelled calls can be retried.

## Configuration

The application binds these fields from `SolarSales`:

| Field | Default | Effect |
|---|---|---|
| `ContractStartDate` | `2026-09-28` | First eligible local contract date |
| `TimeZoneId` | `Europe/Warsaw` | Contract calendar and period boundaries |
| `PayNegativePrices` | `false` | Floors each negative quarter price at zero; `true` uses signed prices and requires the applicable contract amendment |

The device and cloud access use the existing `DeyeCloud` settings, including `DeviceSn`. Configuration may come from application files or environment variables such as `SolarSales__ContractStartDate`; matching `AppSettings` database rows have precedence. Inspect existing overrides before changing defaults. There is no separate sales configuration form. Keep the contract timezone and price policy aligned with the agreement; changing policy recalculates stored observations and signed prices rather than rewriting them.

## Upgrade and rollback

Migration `20260930160000_AddExportReadings` adds `ExportReadings` and `ExportPrices` on both fresh installations and upgrades. It does not seed prices, infer legacy export history or replace installation settings. The application applies migrations at startup.

Before deploying, record the current image identity, back up SQL with checksum verification, and confirm the intended image is the only Compose change. After startup, verify authenticated dashboard access, the migration marker, both tables and their keys/types, preservation of application settings, and unchanged adjacent services. A healthy login page alone does not prove the sales schema is ready.

An application rollback can restore the previous image while preserving both additive tables and their collected data. Do **not** run the migration's `Down` method or restore the database automatically: both can discard observations and prices collected after the backup. Database recovery requires a separate, deliberate decision.
