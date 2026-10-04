# Growatt OSS API

Uses the user's Growatt-issued OSS API token (HTTP `token` header), authorized ShineServer username and regional HTTPS OpenAPI origin. The supported documented family is **MIN/TLX, device type 7**. Other device families are omitted until their distinct telemetry contracts have adapters.

The provider enumerates authorized plants and devices, verifies the serial number returned by `tlx_last_data`, and exposes PV, grid import minus export, load and battery SOC/power when battery presence is established by the reported battery count or serial. A MIN inverter with default zero BMS fields is not treated as a battery system. Battery discharge minus charge uses separate documented BDC power fields.

The published MIN fields do not declare their power units. Configure `powerUnit` as W or kW only after checking the installation's API output; the default Unverified keeps those measurements invalid. Missing values stay missing. A documented calendar epoch is preserved; a text timestamp requires the configured IANA device timezone and is rejected at ambiguous/invalid local times. Battery temperature, voltage and current remain unavailable because the inspected field table does not establish their scales.

Plant inventory is cached for eight hours inside the worker because the endpoint permits ten calls/day. Device inventory and latest readings are cached for five minutes to follow the documented update cadence. Fresh setup workers and process restarts cannot share this cache; vendor limits still apply across them. Excessive pages, inconsistent counts, API errors and unsupported devices fail closed.

Grid history is not advertised: the published `tlx_data` page does not provide a complete, verifiable response/pagination contract. An attempted history operation fails instead of fabricating samples.

Protocol tests use a real mock HTTP server and a separate framed worker process. A live vendor connection still requires user-owned credentials and acceptance against the relevant inverter.

Primary documentation is linked by Growatt's [installer operation manual](https://cdn.growatt.com/pdf/Installer%20Operation%20Manual.pdf):

- [Global transport, regions and token authentication](https://www.showdoc.com.cn/262556420217021/1494054927748011)
- [Authorized plant list](https://www.showdoc.com.cn/262556420217021/1494064249382745)
- [Device list and family IDs](https://www.showdoc.com.cn/262556420217021/6117958613377445)
- [MIN/TLX real-time fields](https://www.showdoc.com.cn/262556420217021/6129822090975531)
- [History specification](https://www.showdoc.com.cn/262556420217021/8559849784929961)
