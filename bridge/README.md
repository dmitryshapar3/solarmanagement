# Home Bridge

The bridge now runs directly on Windows as a single service. It serves the same local admin UI and polls the remote DeyeSolar server from the host network, so TinyTuya discovery happens on the host instead of inside Docker.

## Files

- `service-config.example.json`: service/runtime settings for the bridge process
- `config.example.json`: device config shape for the bridge-managed sockets
- `cloud-config.example.json`: example Tuya cloud settings file used for local-key lookup
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
- `cloud_config_path`: writable Tuya cloud config file path
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

If you want the bridge to fetch `local_key` from Tuya Cloud during scan, create `cloud-config.json` next to the script or save it from the admin UI.

Fields:

- `apiRegion`: Tuya cloud region such as `eu`
- `apiKey`: Tuya project API key
- `apiSecret`: Tuya project API secret
- `apiDeviceId`: optional seed device ID used by some Tuya account layouts

Behavior:

- scan calls Tuya Cloud device detail for each discovered device ID
- if cloud returns a matching key, the result is tagged as `local key from cloud`
- if cloud is not configured or cloud lookup fails, scan still returns the discovered devices without a key
- cloud errors are surfaced in the admin UI and stats cards

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
- ask Tuya Cloud for matching local keys when cloud settings are configured
- add discovered devices into the bridge config
- edit `name`, `device_id`, `local_key`, `category`, `protocol_version`, and `ip`
- save the config without restarting the service

Without a cloud configuration, the scan cannot recover the Tuya `local_key`; you still need to enter that manually.

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
