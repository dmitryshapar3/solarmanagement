# Home Bridge

This container polls the DeyeSolar cluster from your home network and controls Tuya sockets locally with TinyTuya.
It now also serves a local admin page at `/` for scanning the LAN and editing the bridge device config.

## Required Environment Variables

- `DEYE_CLUSTER_BASE_URL`: public base URL of the DeyeSolar app, for example `https://deye.example.com`
- `DEYE_BRIDGE_TOKEN`: bearer token that matches `SocketBackend:BearerToken` in the cluster app

## Optional Environment Variables

- `DEYE_BRIDGE_ID`: bridge identifier, default `home-main`
- `DEYE_BRIDGE_CONFIG`: mounted config path, default `/config/bridge-config.json`
- `DEYE_BRIDGE_STATE`: writable state cache path, default `/state/bridge-state.json`
- `DEYE_BRIDGE_POLL_INTERVAL`: sync interval in seconds, default `2`
- `DEYE_BRIDGE_REQUEST_TIMEOUT`: HTTP timeout in seconds, default `10`
- `DEYE_BRIDGE_SCAN_TIMEOUT`: TinyTuya scan timeout in seconds, default `5`
- `DEYE_BRIDGE_PORT`: local health port, default `8081`

## Config File

Mount a writable JSON config file shaped like `config.example.json`. Each device requires:

- `device_id`
- `local_key`
- `name`
- `category`
- `protocol_version`
- optional `ip`

The bridge runs a startup discovery pass to refresh IPs for configured device IDs and stores refreshed IPs in the state cache file.
The admin UI writes back to this file, so do not mount it read-only.

## Admin UI

Open [http://localhost:8081/](http://localhost:8081/) after the container starts.

The page lets you:

- scan the LAN for Tuya devices
- add discovered devices into the bridge config
- edit `name`, `device_id`, `local_key`, `category`, `protocol_version`, and `ip`
- save the config without restarting the container

Important: the scan cannot recover the Tuya `local_key`. You still need to enter that manually.

## Docker Networking Note

TinyTuya discovery uses LAN broadcast. In Docker, discovery is most reliable when the container can join the LAN directly:

- Linux: prefer `network_mode: host` or a macvlan setup
- Docker Desktop: bridge networking often blocks broadcast discovery, so manual IP entry may still be required

If discovery returns no devices, keep using the UI and enter `device_id`, `local_key`, and `ip` manually.

## Example Run

```bash
docker build -f bridge/Dockerfile -t deye-home-bridge .

docker run --rm \
  -p 8081:8081 \
  -e DEYE_CLUSTER_BASE_URL=https://deye.example.com \
  -e DEYE_BRIDGE_TOKEN=replace-me \
  -e DEYE_BRIDGE_ID=home-main \
  -v "$PWD/bridge-config.json:/config/bridge-config.json" \
  -v "$PWD/bridge-state:/state" \
  deye-home-bridge
```

`GET /` serves the admin page. `GET /healthz` returns the bridge heartbeat status for Docker health checks.
