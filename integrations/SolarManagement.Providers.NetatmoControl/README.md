# Legrand / Netatmo Home + Control socket worker

Provider ID: `netatmo.control`. Public `endpoint` fixed to `https://api.netatmo.com/api`; secret `accessToken`. Use your own Netatmo developer application and an authorized token with `read_magellan` and `write_magellan` scopes. Configure token acquisition/renewal externally; expired or insufficiently scoped credentials fail closed. Legrand devices need their Home + Control gateway. No partner account or user password is used by the worker.

Current official Home + Control OpenAPI v1.3 was read from the developer documentation's public `POST https://dev.netatmo.com/api/getdocumentationjson` endpoint, form `fileName=Legrand`. GET `/homesdata` enumerates topology, GET `/homestatus?home_id=...` reads current state, POST `/setstate` sends JSON `{home:{id,modules:[{id,on,bridge}]}}`. Only types `NLP` (socket), `NLPM` (mobile socket), `NLC` (cable outlet), and `NLPO` (connected contactor) are admitted. Energy-only `NLPC` is excluded. Module IDs and home/bridge routing are taken from the authoritative topology and checked against status responses.

`power` is explicitly watts; `on` and `reachable` are Booleans. `last_seen` is the gateway communication timestamp in Unix seconds, exposed as ObservedAt; it is not an energy sampling guarantee. Missing fields remain null. No contactor's phase count is guessed from its module type. Inventory is bounded to 20 homes and 200 modules/channels, and vendor errors or module synchronization errors fail closed. A command must have `status: "ok"` and no `body.errors`; state is read back before acknowledgement.

The published token-only API used here provides no verified stable account ID. AccountIdentity remains null; device/home IDs and configured account fields are not used as a substitute. The host therefore requires a new integration when credentials are replaced after devices have been registered. Select the devices and recreate their rule links in that new integration. Token expiry/renewal requires this deliberate re-registration until a documented complete account-verification or OAuth flow is implemented.

Official references checked 2026-10-04:
- [Current Home + Control API](https://dev.netatmo.com/apidocumentation/control)
- [Authentication](https://dev.netatmo.com/apidocumentation/oauth)
- [API rate limits](https://dev.netatmo.com/guideline)
- [Legrand migration notice: old Works with Legrand API deprecated](https://developer.legrand.com/solutions/wiring-devices-with-netatmo/)
