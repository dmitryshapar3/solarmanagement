# Shelly Cloud

The adapter uses the account authorization key and the documented HTTPS Cloud Control API. Production transport remains restricted to approved public Shelly HTTPS origins.

Shelly Pro 3EM with a Pro Output Add-on exposes its relay as `switch:100` (add-on IDs are 100–199). Discovery and commands preserve that component ID. For a Pro 3EM device code starting `SPEM-003`, the adapter reads total active power from `em:0.total_act_power` when the add-on relay has no own power reading. Meter CTs must measure the switched circuit for that association to represent its consumption. No inference associates meters with unrelated device types. Missing/negative consumption readings remain unavailable under the existing socket contract.

The relay is the control signal for a suitably rated external contactor. This adapter does not model individual phase readings or enforce electrical interlocks.

Protocol fixtures verify component ID 100, control request bodies and aggregate readings using a real local HTTP server and a framed worker subprocess. Cloud control of add-on channel 100 has not been tested against a physical device or a live account; verify that mapping during device acceptance. The published Cloud API specifies a numeric channel and an invalid-channel error, without a specific add-on example.

Primary specifications:

- https://shelly-api-docs.shelly.cloud/cloud-control-api/communication-v2/
- https://shelly-api-docs.shelly.cloud/gen2/Addons/ShellyProOutputAddon/
- https://shelly-api-docs.shelly.cloud/gen2/ComponentsAndServices/EM/#status
- https://shelly-api-docs.shelly.cloud/gen2/Devices/Gen2/ShellyPro3EM/
