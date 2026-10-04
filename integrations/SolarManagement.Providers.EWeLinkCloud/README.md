# SONOFF / eWeLink socket worker

Provider ID: `ewelink.cloud`. Public `endpoint`; secrets `appId`, `accessToken`. Configure your own eWeLink developer application and an already authorized user access token. OAuth token acquisition/renewal is not implemented here, and the adapter never accepts the user's password or silently impersonates the eWeLink app. Expired or invalid credentials fail closed. Developer APPIDs have vendor approval and quota requirements; free APPID is 50,000 API requests per month per region according to the documented API. Brands besides SONOFF/CoolKit may require vendor authorization.

REST device inventory uses `/v2/device/thing?num=30`. Exact UIID protocols are restricted to documented single-channel `1,5,6,32` and multi-channel `2,3,4,7,8,9`; unknown/new UIIDs are omitted. UIIDs 138–141 are only named in the public type list, so their undocumented command schemas are excluded. Channels are `0..3`, and multi-channel controls address only the selected `outlet`. Independent channels are never classified as jointly switched three-phase equipment. UIID 5/32 `power` is in watts; all other power values remain null rather than extrapolating undocumented models. New S60 firmware/model variants may expose different UIIDs and require an official protocol mapping before addition.

The initial implementation supports at most 30 inventory items, including groups; a larger or partially authorized inventory fails rather than disappearing devices from a partial result. Requests are throttled to one per second. Command API is POST `/v2/device/thing/status` with exact single/multi UIID parameters. Vendor `error: 0` is required; after command acceptance a refreshed inventory must confirm state before acknowledgement. Power is the latest cloud-reported value: UIID 5/32 only reports measurements during its documented activation period, and this adapter does not start a `uiActive` session. The value can be stale. Reported cloud state has no verified measurement timestamp, so ObservedAt remains null.

Test/discovery also call GET `/v2/user/profile`. The documented `user.apikey` is the user ID; its verified response value is hashed into AccountIdentity. Same-account token renewal is possible after disabling the integration, provided the application can read User Profile. Profile permission errors or missing IDs fail closed. Device-owner IDs are not substituted for the authenticated account ID.

Official references checked 2026-10-04:
- [OAuth2, device API, quotas and approval](https://github.com/CoolKit-Technologies/eWeLink-API/blob/main/en/OAuth2.0.md)
- [UIID protocols and units](https://github.com/CoolKit-Technologies/eWeLink-API/blob/main/en/UIIDProtocol.md)
- [Developer console](https://dev.ewelink.cc/)
- [Authoritative user profile and user ID](https://github.com/CoolKit-Technologies/eWeLink-API/blob/main/en/APICenterV2.md)
