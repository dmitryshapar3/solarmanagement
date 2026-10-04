# Aqara Cloud socket worker

Provider ID: `aqara.cloud`. Public `endpoint`; secrets `appId`, `keyId`, `appKey`, `accessToken`. Create your own reviewed project in the Aqara developer console, pair devices through Aqara Home, and authorize the account. This implementation uses a configured token; no password login, partner account, or incomplete OAuth browser flow is added. Expired tokens must be renewed externally. Zigbee plugs require an Aqara hub. Model resource availability is project/region-specific.

Official HTTP POST `/v3.0/open/api` intents: `query.device.info` (50-item pages, at most 200 devices), `query.resource.info`, `query.resource.value`, `write.resource.device`. The request signature sorts header parameters as `Accesstoken,Appid,Keyid,Nonce,Time`, appends AppKey, lowercases the whole string and computes MD5 according to Aqara's published authentication protocol. MD5 is only this vendor-mandated signature, not a password storage mechanism.

Supported topology families are `lumi.plug.*`, `lumi.switch.*`, `lumi.ctrl_*`. Switch resources are explicit `4.N.85` values with enum `0,1` and access 5/7 (read+write). Each independent channel keeps its exact resourceId. Resources not opened to the application are omitted. A wall switch with three channels is not marked three-phase. Discovery is bounded to 200 channels, detects incomplete/changed pagination, and rejects mismatched/duplicate resource identities.

Power resource `0.12.85` is used only if metadata explicitly says `unit: "W"`; public legacy docs expose numeric unit identifiers without a conversion table, so these remain null and do not advertise a power capability. No undocumented scale is guessed. Switch resource `timeStamp` is milliseconds since Unix epoch. Unknown value/reachability remains null. Vendor `code: 0` is required and desired state is read back before acknowledgement.

The published token-only API used here provides no verified stable account ID. AccountIdentity remains null; device/home IDs and configured account fields are not used as a substitute. The host therefore requires a new integration when credentials are replaced after devices have been registered. Select the devices and recreate their rule links in that new integration. Token expiry/renewal requires this deliberate re-registration until a documented complete account-verification or OAuth flow is implemented.

Official references checked 2026-10-04:
- [Project review and account authorization](https://opendoc.aqara.com/en/docs/developmanual/manageApplication/manageProject.html)
- [Signature rules](https://opendoc.aqara.com/en/docs/developmanual/apiIntroduction/signGenerationRules.html)
- [Device management and pagination](https://opendoc.aqara.com/en/docs/developmanual/apiDocument/DeviceManagement.html)
- [Resources, permission flags and switch values](https://opendoc.aqara.com/en/docs/developmanual/apiDocument/ResourceManagement.html)
- [Aqara account OAuth](https://opendoc.aqara.com/en/docs/developmanual/authManagement/aqaraauthMode.html)
