# Home Bridge

The bridge now runs directly on Windows as a single service. It serves the same local admin UI and polls the remote DeyeSolar server from the host network, so TinyTuya discovery happens on the host instead of inside Docker.

## Files

- `service-config.example.json`: service/runtime settings for the bridge process
- `config.example.json`: device config shape for the bridge-managed sockets
- `cloud-config.example.json`: optional Tuya Cloud credentials used to enrich scan results with local keys
- `local_key_hook.py`: local key acquisition hook called during scan for each discovered device IP
- `windows_service.py`: the Windows service entrypoint
- `discovery_worker.py`: one-shot TinyTuya discovery worker launched outside the service session
- `install-service.ps1`: installs or reinstalls the service
- `uninstall-service.ps1`: stops and removes the service
- `start-bridge.ps1`: starts the bridge as a single background host process
- `stop-bridge.ps1`: stops that background host process

## Service Config

Create `service-config.json` next to the script. The install script will copy the example if it is missing.

Important fields:

- `cluster_base_url`: remote DeyeSolar URL, default example points at `http://157.250.198.4:30880`
- `bridge_id`: bridge identifier, default `home-main`
- `bridge_token`: shared secret that must match the remote app
- `bridge_config_path`: writable device config file path
- `bridge_state_path`: writable state cache file path
- `cloud_config_path`: optional writable cloud credential file used for local-key enrichment during scan
- `local_key_hook_timeout`: max seconds to wait for `local_key_hook.resolve_local_key()`, default `30`
- `discovery_mode`: `task-helper` uses the one-shot discovery worker task, `direct` scans in-process
- `discovery_task_name`: scheduled task name used for discovery, default `DeyeHomeBridgeDiscovery`
- `bind_host`: local listen address, default `127.0.0.1`
- `port`: local admin/API port, default `8081`

The service reads `service-config.json`, maps those settings into the bridge runtime, and starts the local FastAPI UI/API.

## Device Config

The bridge device config file follows `config.example.json`. Each device needs:

- `device_id`
- `local_key`
- `name`
- optional `category`
- optional `protocol_version`
- optional `ip`

The admin UI writes this file directly, and the bridge keeps the state cache file updated with refreshed IPs.

## Cloud Key Assist

If you want scan to fill `local_key` automatically, configure Tuya Cloud access.

The bridge stores that optional config in `cloud-config.json`. The admin UI can save it directly, or you can create it from `cloud-config.example.json`.

Fields:

- `apiRegion`: Tuya API region such as `eu`, `us`, `us-e`, `eu-w`, `in`, `sg`, or `cn`
- `apiKey`: Tuya IoT project API Access ID
- `apiSecret`: Tuya IoT project API Access Secret
- `apiDeviceID`: optional sample device ID; leave blank unless your Tuya project needs it

Requirements:

- the device must already be paired in `Smart Life` or `Tuya Smart`
- your app account must be linked to the same Tuya IoT cloud project
- the cloud project must be allowed to list the device

With that configured, `Scan Now` will still do LAN discovery for IP/version, then enrich matching devices from Tuya Cloud so the result row can include `local_key`. The scan request also uses the current Cloud Key Assist values from the page, so you do not need a separate setup step before the first scan.

## Local Key Hook

If you want to supply `local_key` from your own lab tooling instead of Tuya Cloud, implement the placeholder in `bridge/local_key_hook.py`.

The bridge calls:

- `resolve_local_key(request: LocalKeyRequest) -> None | str | LocalKeyResult`

Request fields:

- `ip`: discovered device IP address
- `device_id`: discovered Tuya device ID
- `name`: discovered device name, if available
- `category`: discovered Tuya category, if available
- `product_key`: discovered product key, if available
- `protocol_version`: discovered LAN protocol version
- `raw_scan_result`: the raw scan payload for that device

Return contract:

- `None`: no key available for this device
- `str`: resolved `local_key`
- `LocalKeyResult(local_key=..., source=...)`: resolved key plus a short source label shown in the UI

Behavior:

- the bridge calls the hook once per scanned device that still has no `local_key`
- the bridge waits synchronously for the result
- the wait is bounded by `local_key_hook_timeout`
- if the hook raises, the scan keeps going and the error is surfaced in the admin UI/log
- if the hook returns a key, the scan result is tagged as `local key from hook`

Resolution order during scan:

1. LAN discovery finds IP/device metadata
2. `local_key_hook.py` gets the first chance to provide `local_key`
3. Tuya Cloud enrichment runs only if the device still has no key

## Install

Run:

```powershell
powershell -ExecutionPolicy Bypass -File bridge/install-service.ps1
```

That script:

- installs Python requirements, including `pywin32`
- creates `service-config.json` from the example if needed
- registers an on-demand `DeyeHomeBridgeDiscovery` scheduled task under your user session
- reinstalls the `DeyeHomeBridge` Windows service as `LocalSystem`
- starts the service

Run it from an elevated PowerShell window. The service itself runs as `LocalSystem`; only the discovery helper task is registered under your interactive Windows user.

If `service-config.json` was just created, fill in the token if needed and run the script again.

## UI

After the service starts, open [http://127.0.0.1:8081/](http://127.0.0.1:8081/).

The page lets you:

- scan the LAN for Tuya devices
- enrich scan results with local keys from `local_key_hook.py` when your custom hook returns one
- enrich scan results with local keys from Tuya Cloud when Cloud Key Assist is configured
- add discovered devices into the bridge config
- edit `name`, `device_id`, `local_key`, `category`, `protocol_version`, and `ip`
- save the config without restarting the service

Without Cloud Key Assist, the scan cannot recover the Tuya `local_key`; you still need to enter that manually.

Discovery note:

- the Windows service itself runs in the service session, which can miss TinyTuya UDP broadcasts
- the bridge now solves that by starting a separate one-shot discovery worker through Task Scheduler in your interactive user session
- once the worker writes the discovery result file, it exits immediately

## Manage

Foreground debug run:

```powershell
python bridge/windows_service.py run
```

Background host process:

```powershell
powershell -ExecutionPolicy Bypass -File bridge/start-bridge.ps1
```

Stop the background host process:

```powershell
powershell -ExecutionPolicy Bypass -File bridge/stop-bridge.ps1
```

Remove the service:

```powershell
powershell -ExecutionPolicy Bypass -File bridge/uninstall-service.ps1
```
