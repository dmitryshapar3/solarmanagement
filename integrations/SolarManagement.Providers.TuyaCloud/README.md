# Tuya / Smart Life socket worker

Provider ID: `tuya.cloud`. Credentials: public `endpoint`, `userId`; secrets `accessId`, `accessSecret`. No partner or OEM OAuth flow is required by this implementation: link your own supported app account to your own Tuya cloud project in the Tuya console. Cloud services must be enabled for the project and match the account region. IoT Core trial is for development, and Tuya prohibits commercial use of trial plans.

The adapter signs both token and business requests using the documented HMAC-SHA256 canonical method (uppercase digest, lowercase body SHA256, empty signature-header line). It obtains a client-credentials token using `/v1.0/token?grant_type=1` and uses user inventory `/v1.0/users/{uid}/devices`, specification `/v1.2/iot-03/devices/{id}/specification`, status `/v1.0/iot-03/devices/{id}/status`, and commands `/v1.0/iot-03/devices/{id}/commands`.

Supported standard product categories: socket `cz`, power strip `pc`, circuit breaker `dlq`. Only explicitly writable Boolean `switch` / `switch_N` DPs are discovered, with each channel identity equal to its exact DP code. A breaker is not automatically classified as three-phase. Three relay channels are independent circuits. `cur_power` is converted only if device specification explicitly gives unit `W` and a valid scale. Total power is never duplicated across independent channels. Unknown state, reachability, power or measurement timestamp remains null.

Inventory has a maximum of 200 physical devices and 200 switch channels; malformed, duplicate or excessive results fail closed. A successful command response must contain Boolean `result: true`; readback must confirm desired state before acknowledgement. Readback failure is uncertain. Production uses the guarded CloudHttpClient; injected HttpClient supports deterministic tests and retains official endpoint/config validation.

Inventory explicitly requests 50-item pages and verifies a final short/empty page, with a 200-device bound. A full 200-item inventory requires an empty fifth page; another item or duplicate paging identities fails closed.

Test/discovery also call the authorized user-profile endpoint `/v1.0/users/{uid}/infos`. The response UID must match the requested user; only its verified value is hashed into AccountIdentity. This permits same-account credential renewal after disabling the integration, and fails closed if the profile API is unavailable or inconsistent. Enable the User Information API for the cloud project.

Official references checked 2026-10-04:
- [Smart Home Quick Start](https://developer.tuya.com/en/docs/iot/smart-home-quick-start?id=Kbvwrxn6mngbd)
- [Signing requests](https://developer.tuya.com/en/docs/iot/new-singnature?id=Kbw0q34cs2e5g)
- [Device specification](https://developer.tuya.com/en/docs/cloud/85b8f49180?id=Kb5rg8mvrccb7)
- [User device inventory and pagination](https://developer.tuya.com/en/docs/cloud/ad2823ae46?id=Kconjtzq1vk1q)
- [Socket DP definition](https://developer.tuya.com/en/docs/iot/product-standard-function-introduction?id=K9tp15ceh63gr)
- [Pricing and trial restriction](https://developer.tuya.com/en/docs/iot/membership-service?id=K9m8k45jwvg9j)
- [Authoritative user profile](https://developer.tuya.com/en/docs/cloud/cfebf22ad3?id=Kawfjdgic5c0w)
