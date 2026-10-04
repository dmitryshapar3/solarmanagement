# Provider protocol end-to-end fixtures

Run `dotnet test tests/SolarManagement.ProviderE2E/SolarManagement.ProviderE2E.csproj`.

Each test starts a real localhost HTTP server, launches the production provider class inside `IntegrationWorkerHost` in a separate process, and communicates using the real length-framed JSON protocol. The fixture validates HTTP methods, paths, credentials/signatures and payloads from linked primary vendor specifications. Tests cover normalized measurements and actual on/off command requests, including negative cases.

Only the test launcher injects an HTTP transport which sends official URLs to the loopback fixture. Production provider entry points still use `CloudHttpClient` with approved public HTTPS origins. No production environment variable, user setting or security bypass enables this transport.

Passing these tests demonstrates compatibility with the documented protocol fixtures. It does not demonstrate a live connection to a vendor account or physical device. Live acceptance requires user-owned vendor API credentials and relevant hardware, especially Shelly Pro Output Add-on cloud channel mapping.
