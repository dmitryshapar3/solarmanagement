# Huawei FusionSolar worker

Manufacturer SmartPVMS 25.2 northbound monitoring contract. Supply either a dedicated API account (`authMode=apiAccount`, `username`, secret `systemCode`) created by the installer/company administrator and bound to the plants, or an approved owner's OAuth `accessToken` (`authMode=accessToken`). An ordinary FusionSolar portal account is not automatically an API account. OAuth app registration/approval and owner consent are external prerequisites. This worker consumes the token and does not advertise interactive OAuth or renewal.

API account authentication POSTs `thirdData/login`, captures response-header `XSRF-TOKEN` and reuses it for up to 29 minutes (vendor validity 30). A rejected login is not repeatedly replayed within the worker generation. The vendor limits logins to five per ten minutes and locks repeated invalid-password attempts. OAuth mode uses Authorization Bearer.

Implemented: paged `thirdData/stations`; `getDevList`; `getDevRealKpi`; grid-meter `getDevHistoryKpi`. Types 1 and 38 are inverters. `mppt_power` is PV DC kW, converted to W. Type 39 battery provides SOC %, busbar V and charge/discharge W. Type 17 grid meter provides total `active_power` W. Signed grid/battery directions require explicit verified configuration; positive import/discharge is the application convention. Load, battery temperature and battery current stay missing because this implementation has no verified source for them.

The device list does not declare an inverter-parent relationship. A plant's battery or grid meter is associated automatically only when its plant contains one inverter and one matching device. Optional `batteryDeviceId` / `gridDeviceId` must match the type and plant in the authorized inventory. Ambiguous associations stay absent. Type 47 power sensor and EMMA are not silently substituted for type 17 because their point/unit contracts differ.

The live API supplies no measurement collection timestamp. Its `params.currentTime` is a server response time, so live `ObservedAt` deliberately stays null; host rules requiring fresh evidence cannot safely act on this live source. Grid history supplies `collectTime` epoch milliseconds and is split into at most three-day requests, up to seven days total. Missing/invalid samples, empty chunks and conflicting duplicates mark history incomplete. Devices returned under another identity are rejected.

Production uses the SDK HTTPS-443 origin/DNS protections without cookies, portal scraping or redirects. The injected HttpClient constructor keeps configuration-origin validation for real process fixtures.

Manufacturer reference and access instructions:

- [Official current northbound API reference](https://support.huawei.com/enterprise/en/doc/EDOC1100492747/8f6c35d4/change-history)
- [API access](https://support.huawei.com/enterprise/en/doc/EDOC1100492747/dba0b7c8/api-access)
- [API account access](https://support.huawei.com/enterprise/en/doc/EDOC1100492747/1d58596/api-account-access)
- [OAuth Connect](https://support.huawei.com/enterprise/en/doc/EDOC1100492747/95c18db0/oauth-connect)
- [Readable copy of Huawei's SmartPVMS 25.2.0 reference, issued 2025-07-28](https://www.scribd.com/document/980589962/SmartPVMS-25-2-0-Northbound-API-Reference)

Wire schemas and units were checked against the manufacturer's reference (sections 3 and 5); the readable mirror is included to make those exact sections reviewable when Huawei's dynamic viewer is unavailable. Package digests must be produced by repository packaging tooling before installing the source manifest.
