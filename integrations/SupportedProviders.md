# Provider integrations

Each provider is an independent executable project using the existing WorkerSdk and inverter/socket contracts. Web and mobile load its setup form from its signed manifest; provider assemblies are not referenced by the Web project.

| Kind | New provider | Supported interface |
| --- | --- | --- |
| Inverter | [SolisCloud](SolarManagement.Providers.SolisCloud/README.md) | Owner-authorized OAuth monitoring API |
| Inverter | [Sungrow](SolarManagement.Providers.SungrowCloud/README.md) | iSolarCloud type 14 storage inverter monitoring |
| Inverter | [Huawei](SolarManagement.Providers.HuaweiFusionSolar/README.md) | FusionSolar northbound API account or supplied OAuth token |
| Inverter | [Growatt](SolarManagement.Providers.GrowattCloud/README.md) | OSS token API, MIN/TLX type 7 |
| Socket | [Tuya](SolarManagement.Providers.TuyaCloud/README.md) | Linked cloud-project devices and documented switch functions |
| Socket | [eWeLink](SolarManagement.Providers.EWeLinkCloud/README.md) | v2 Open API, documented outlet/relay models |
| Socket | [Aqara](SolarManagement.Providers.AqaraCloud/README.md) | Open Platform resources and write acknowledgements |
| Socket | [Legrand/Netatmo](SolarManagement.Providers.NetatmoControl/README.md) | Netatmo Control sockets and relays |

Deye and Shelly remain supported. [Shelly](SolarManagement.Providers.ShellyCloud/README.md) also recognizes documented peripheral relay IDs 100–199 for Pro Output Add-on installations. GoodWe and Tapo are deferred until an accessible protocol contract is available.

Read each provider's prerequisites before setup. A portal login alone may not grant API access. Supplied access tokens require external authorization and renewal; these workers do not advertise an interactive OAuth flow. Model, unit, timestamp and history limitations are listed in each project.

## Build and install

Run `dotnet build DeyeSolar.sln`. The existing `tools/Build-IntegrationDeployment.ps1` now discovers every provider project, publishes each separately and signs its manifest using the supplied publisher key. It writes all package archives and the approved origins to `integration-bootstrap.json`. The two original manifests remain in `integrations/manifests`; new manifests are inside their provider projects. Never install the unsigned source manifests directly. Existing package admission verifies publisher signatures, hashes, origins and descriptors.

The original Deye and Shelly packages are `1.0.1`; new providers are `1.0.0`. Retain and deploy the exact signed release bundle. Rebuilding published packages requires fresh versions, and existing connections keep their previous artifacts until explicitly upgraded; see [deployment and backup instructions](README.md).

In Integrations, choose an inverter or socket manufacturer and add its account. Save the credentials, discover devices, select them and enable the account. Additional socket accounts can use different manufacturers. For every selected socket, choose its linked inverter and circuit type, then save the link. Several sockets from different accounts can share one inverter. The circuit type records the installed single-phase or three-phase load; it does not establish a device's electrical ratings or add phase measurements to the existing contract.

Rules with an explicit source keep using that source. Rules without one use the socket's link, then the installation default when the link is empty. Source edits are tenant-scoped and guarded against stale forms and commands already in progress. Automatic commands check the evaluated source again within the durable command transaction.

## Verification

Run `dotnet test tests/SolarManagement.ProviderE2E/SolarManagement.ProviderE2E.csproj` for vendor protocol tests and signed-package admission. The tests run production provider classes in a separate framed worker process against a real local HTTP fixture. Web tests exercise persisted source associations and automatic mixed-provider rules; mobile component tests exercise the actual manufacturer dropdowns and guarded save/unlink requests.

Protocol fixtures verify the documented wire contracts; live acceptance requires vendor credentials and hardware. The existing SQL Server suite requires `SOLAR_TEST_SQL_CONNECTION`; new SQLite association tests run without it.

Verified locally on 2026-10-04 after combining the provider work with the existing 15-language release: 946 .NET tests passed, including 88 provider process/HTTP/package tests and 10 SQLite source-association tests. The 99 existing SQL Server tests were skipped because that service was unavailable. All 173 mobile tests, TypeScript checking and iOS export passed. Each of the 10 providers was also independently published and signed with a temporary test publisher key; Web publication contained no provider or WorkerSdk assemblies. The temporary private key was removed.

The PowerShell deployment script is exercised by the package CI workflow. Local publication/signing used the same `dotnet publish` and packager operations because PowerShell was unavailable on this host. No production packages were installed and no vendor sockets were operated.
