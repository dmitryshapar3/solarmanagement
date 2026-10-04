# SolisCloud worker

Official owner-authorized OAuth monitoring API on `https://api-oauth2.soliscloud.com` (HTTPS 443). Supply the approved owner's `accessToken` secret. An ordinary portal password is insufficient; Solis requires API activation/developer approval. This worker consumes an existing token and does not advertise an interactive OAuth flow or renew tokens. Authorization-code integration is not enabled because the published contract does not establish the host's required PKCE S256 behavior.

Implemented: cursor-paged `inverterList`, selected-device `inverterDetail`, and plant-level `stationDay` grid history. Detail identity must match the requested SN. PV DC power is the sum of documented PV channel voltage/current pairs. SOC is a percentage. Power unit strings determine W/kW/MW conversion; unknown units are invalid, absent values are missing. The modern OAuth table omits unit fields: when none is returned, power requires an explicitly verified `livePowerUnit` configuration (W or kW); its default is unknown. Set `gridPositiveDirection` and `batteryPositiveDirection` only after verifying the installation/vendor convention; unknown direction suppresses signed power. Battery power uses positive discharge; grid power uses positive import, negative export.

History requires the selected device's station, an explicit verified `historyPowerUnit`, grid direction, and the plant's integer UTC offset. This API returns plant totals, so multiple inverters in one plant share its grid history. Up to seven days are split into local day requests. Invalid timestamps, powers, conflicting duplicates, or empty results mark history incomplete. Missing collection timestamps stay null; receipt time is never substituted. The nominal vendor collection period is five minutes.

Only the OAuth endpoint is included. The separate customer HMAC API on port 13333 is deliberately absent because the existing host transport allows HTTPS 443. Production uses the SDK origin/DNS/redirect protections. The injected HttpClient constructor retains the same configuration-origin validation and exists for framed-process HTTP fixtures.

Sources:

- [Quick start / access prerequisites](https://developer.soliscloud.com/guide/quick-start.html)
- [OAuth data API, pagination, current data and history](https://developer.soliscloud.com/guide/data-access-oauth2.html)
- [OAuth authorization](https://developer.soliscloud.com/guide/authorization.html)
- [Manufacturer API reference V2.0.2, units and response fields](https://oss.soliscloud.com/templet/SolisCloud%20Platform%20API%20Document%20V2.0.2.pdf)

Build with `dotnet build integrations/SolarManagement.Providers.SolisCloud`. Publish/sign/package with the repository's integration package tooling; `manifest.json` is a source template, whose file and descriptor digests must be generated for an installable package.
