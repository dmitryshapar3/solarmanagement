import asyncio
import json
import logging
import os
import socket
import subprocess
import time
import uuid
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import httpx
import tinytuya
from fastapi import FastAPI, HTTPException
from fastapi.responses import HTMLResponse


BASE_DIR = Path(__file__).resolve().parent
BRIDGE_VERSION = "1.1.0"
ADMIN_UI_PATH = BASE_DIR / "admin.html"
DISCOVERY_HINT = (
    "Network scan discovers device IDs, IPs, and protocol hints. "
    "If Tuya Cloud is configured, scan asks it for matching local keys."
)
LOGGER = logging.getLogger("deye-home-bridge")


def get_env(name: str, default: str | None = None, required: bool = False) -> str:
    value = os.getenv(name, default)
    if required and not value:
        raise RuntimeError(f"Missing required environment variable: {name}")
    return value or ""


@dataclass
class DeviceConfig:
    device_id: str
    local_key: str
    name: str
    category: str | None = None
    protocol_version: str = "3.3"
    ip: str | None = None

    @classmethod
    def from_dict(cls, payload: dict[str, Any]) -> "DeviceConfig":
        device_id = str(payload.get("device_id", "")).strip()
        local_key = str(payload.get("local_key", "")).strip()
        name = str(payload.get("name", "")).strip()

        if not device_id or not local_key or not name:
            raise ValueError("Each device requires device_id, local_key, and name.")

        category = payload.get("category")
        protocol_version = str(payload.get("protocol_version", "3.3")).strip() or "3.3"
        ip = str(payload.get("ip", "")).strip() or None

        return cls(
            device_id=device_id,
            local_key=local_key,
            name=name,
            category=str(category).strip() if category is not None and str(category).strip() else None,
            protocol_version=protocol_version,
            ip=ip,
        )

    def to_dict(self) -> dict[str, Any]:
        return {
            "device_id": self.device_id,
            "local_key": self.local_key,
            "name": self.name,
            "category": self.category or "",
            "protocol_version": self.protocol_version,
            "ip": self.ip or "",
        }


@dataclass
class CloudKeyConfig:
    api_region: str
    api_key: str
    api_secret: str
    api_device_id: str | None = None

    @classmethod
    def from_dict(cls, payload: dict[str, Any]) -> "CloudKeyConfig":
        api_region = str(payload.get("apiRegion") or payload.get("api_region") or "").strip()
        api_key = str(payload.get("apiKey") or payload.get("api_key") or "").strip()
        api_secret = str(payload.get("apiSecret") or payload.get("api_secret") or "").strip()
        api_device_id = str(payload.get("apiDeviceId") or payload.get("api_device_id") or "").strip() or None

        if api_region or api_key or api_secret or api_device_id:
            if not api_region or not api_key or not api_secret:
                raise ValueError("Tuya Cloud requires apiRegion, apiKey, and apiSecret.")

        return cls(
            api_region=api_region,
            api_key=api_key,
            api_secret=api_secret,
            api_device_id=api_device_id,
        )

    def is_configured(self) -> bool:
        return bool(self.api_region and self.api_key and self.api_secret)

    def to_dict(self) -> dict[str, Any]:
        return {
            "apiRegion": self.api_region,
            "apiKey": self.api_key,
            "apiSecret": self.api_secret,
            "apiDeviceId": self.api_device_id or "",
        }

class BridgeRuntime:
    def __init__(self) -> None:
        self.cluster_base_url = get_env("DEYE_CLUSTER_BASE_URL", required=True).rstrip("/")
        self.bridge_id = get_env("DEYE_BRIDGE_ID", "home-main")
        self.bridge_token = get_env("DEYE_BRIDGE_TOKEN", required=True)
        self.config_path = Path(get_env("DEYE_BRIDGE_CONFIG", str(BASE_DIR / "bridge-config.json")))
        self.state_path = Path(get_env("DEYE_BRIDGE_STATE", str(BASE_DIR / "bridge-state.json")))
        self.cloud_config_path = Path(get_env("DEYE_BRIDGE_CLOUD_CONFIG", str(BASE_DIR / "cloud-config.json")))
        self.poll_interval = max(1.0, float(get_env("DEYE_BRIDGE_POLL_INTERVAL", "2")))
        self.request_timeout = max(1.0, float(get_env("DEYE_BRIDGE_REQUEST_TIMEOUT", "10")))
        self.scan_timeout = max(1, int(float(get_env("DEYE_BRIDGE_SCAN_TIMEOUT", "15"))))
        self.discovery_mode = get_env("DEYE_BRIDGE_DISCOVERY_MODE", "direct").strip().lower() or "direct"
        self.discovery_task_name = get_env("DEYE_BRIDGE_DISCOVERY_TASK_NAME", "DeyeHomeBridgeDiscovery")
        self.discovery_request_path = Path(
            get_env("DEYE_BRIDGE_DISCOVERY_REQUEST_PATH", str(BASE_DIR / "discovery-request.json"))
        )
        self.discovery_result_path = Path(
            get_env("DEYE_BRIDGE_DISCOVERY_RESULT_PATH", str(BASE_DIR / "discovery-result.json"))
        )
        self.discovery_wait_timeout = max(
            float(self.scan_timeout) + 5.0,
            float(get_env("DEYE_BRIDGE_DISCOVERY_WAIT_TIMEOUT", str(float(self.scan_timeout) + 10.0))),
        )
        self.devices: dict[str, DeviceConfig] = {}
        self.completed_commands: list[dict[str, Any]] = []
        self.last_sync_at: str | None = None
        self.last_error: str | None = None
        self.last_scan_at: str | None = None
        self.last_scan_results: list[dict[str, Any]] = []
        self.cloud_config = self.load_cloud_config()
        self.last_cloud_lookup_at: str | None = None
        self.last_cloud_lookup_error: str | None = None
        self._task: asyncio.Task[None] | None = None
        self._stop_event = asyncio.Event()

    async def start(self) -> None:
        self.load_config()
        if self.devices:
            await asyncio.to_thread(self.refresh_device_ips)
        self._stop_event.clear()
        self._task = asyncio.create_task(self._run_loop())

    async def stop(self) -> None:
        self._stop_event.set()
        if self._task is not None:
            await self._task

    def read_config_payload(self) -> dict[str, Any]:
        if not self.config_path.exists():
            return {"devices": []}

        try:
            payload = json.loads(self.config_path.read_text())
        except json.JSONDecodeError as exc:
            LOGGER.warning("Config file is invalid JSON, treating as empty: %s", exc)
            return {"devices": []}

        if not isinstance(payload, dict):
            LOGGER.warning("Config file is not an object, treating as empty.")
            return {"devices": []}

        devices = payload.get("devices", [])
        if not isinstance(devices, list):
            LOGGER.warning("Config file devices payload is not a list, treating as empty.")
            return {"devices": []}

        return {"devices": devices}

    def write_config_payload(self, payload: dict[str, Any]) -> None:
        self.config_path.parent.mkdir(parents=True, exist_ok=True)
        self.config_path.write_text(json.dumps(payload, indent=2, sort_keys=True))

    def read_cloud_config_payload(self) -> dict[str, Any]:
        if not self.cloud_config_path.exists():
            return {}

        try:
            payload = json.loads(self.cloud_config_path.read_text())
        except json.JSONDecodeError as exc:
            LOGGER.warning("Cloud config file is invalid JSON, treating as empty: %s", exc)
            return {}

        if not isinstance(payload, dict):
            LOGGER.warning("Cloud config file is not an object, treating as empty.")
            return {}

        return payload

    def load_cloud_config(self) -> CloudKeyConfig | None:
        payload = self.read_cloud_config_payload()
        try:
            config = CloudKeyConfig.from_dict(payload)
        except ValueError as exc:
            LOGGER.warning("Cloud config is invalid, treating as disabled: %s", exc)
            return None

        return config if config.is_configured() else None

    def save_cloud_config(self, config: CloudKeyConfig | None) -> None:
        self.cloud_config = config if config and config.is_configured() else None

        if self.cloud_config is None:
            if self.cloud_config_path.exists():
                self.cloud_config_path.unlink()
            self.last_cloud_lookup_error = None
            return

        self.cloud_config_path.parent.mkdir(parents=True, exist_ok=True)
        self.cloud_config_path.write_text(json.dumps(self.cloud_config.to_dict(), indent=2, sort_keys=True))
        self.last_cloud_lookup_error = None

    def load_config(self) -> None:
        payload = self.read_config_payload()
        cached_ips = self.load_state_cache()
        loaded: dict[str, DeviceConfig] = {}

        for item in payload.get("devices", []):
            try:
                config = DeviceConfig.from_dict(item)
            except ValueError as exc:
                LOGGER.warning("Skipping invalid device config entry: %s", exc)
                continue

            if config.device_id in cached_ips and not config.ip:
                config.ip = cached_ips[config.device_id]
            loaded[config.device_id] = config

        self.devices = loaded
        LOGGER.info("Loaded %s configured device(s)", len(self.devices))

    def cloud_state(self) -> dict[str, Any]:
        config = self.cloud_config or CloudKeyConfig("", "", "", None)
        return {
            "path": str(self.cloud_config_path),
            "configured": config.is_configured(),
            "apiRegion": config.api_region,
            "apiKey": config.api_key,
            "apiSecret": config.api_secret,
            "apiDeviceId": config.api_device_id or "",
            "lastLookupAt": self.last_cloud_lookup_at,
            "lastLookupError": self.last_cloud_lookup_error,
        }

    def save_config(self) -> None:
        payload = {
            "devices": [
                config.to_dict()
                for config in sorted(self.devices.values(), key=lambda item: item.name.lower())
            ]
        }
        self.write_config_payload(payload)

    def load_state_cache(self) -> dict[str, str]:
        if not self.state_path.exists():
            return {}

        try:
            payload = json.loads(self.state_path.read_text())
        except json.JSONDecodeError:
            LOGGER.warning("State cache is invalid JSON, ignoring %s", self.state_path)
            return {}

        devices = payload.get("devices", {})
        if not isinstance(devices, dict):
            return {}

        cached: dict[str, str] = {}
        for device_id, item in devices.items():
            if isinstance(item, dict):
                ip = str(item.get("ip", "")).strip()
                if ip:
                    cached[str(device_id)] = ip
        return cached

    def save_state_cache(self) -> None:
        self.state_path.parent.mkdir(parents=True, exist_ok=True)
        payload = {
            "devices": {
                device_id: {"ip": config.ip}
                for device_id, config in self.devices.items()
                if config.ip
            }
        }
        self.state_path.write_text(json.dumps(payload, indent=2, sort_keys=True))

    def replace_devices(self, devices: list[DeviceConfig], refresh_ips: bool = True) -> None:
        cached_ips = self.load_state_cache()
        updated: dict[str, DeviceConfig] = {}

        for config in devices:
            if not config.ip and config.device_id in cached_ips:
                config.ip = cached_ips[config.device_id]
            updated[config.device_id] = config

        self.devices = updated
        self.save_config()
        if refresh_ips and self.devices:
            self.refresh_device_ips()
        else:
            self.save_state_cache()

        LOGGER.info("Replaced bridge config with %s device(s)", len(self.devices))

    def _scan_raw(self) -> dict[str, Any]:
        if self.discovery_mode == "task-helper":
            return self._scan_via_task_helper()
        discovered = tinytuya.deviceScan(False, self.scan_timeout)
        if not isinstance(discovered, dict):
            raise RuntimeError("TinyTuya discovery returned an unexpected payload.")
        return discovered

    @staticmethod
    def _format_cloud_error(payload: Any) -> str:
        if isinstance(payload, str):
            return payload
        if not isinstance(payload, dict):
            return f"Unexpected Tuya Cloud payload: {type(payload).__name__}"

        code = (
            payload.get("Code")
            or payload.get("code")
            or payload.get("Err")
            or payload.get("err")
            or payload.get("status")
        )
        message = (
            payload.get("Payload")
            or payload.get("msg")
            or payload.get("Message")
            or payload.get("Error")
            or payload.get("message")
            or payload.get("detail")
        )
        if isinstance(message, (dict, list)):
            message = json.dumps(message, sort_keys=True)
        if code and message:
            return f"Code {code}: {message}"
        if message:
            return str(message)
        if code:
            return f"Code {code}"
        return json.dumps(payload, sort_keys=True)

    def _create_cloud_client(self) -> tinytuya.Cloud:
        if self.cloud_config is None:
            raise RuntimeError("Tuya Cloud is not configured.")

        try:
            client = tinytuya.Cloud(
                apiRegion=self.cloud_config.api_region,
                apiKey=self.cloud_config.api_key,
                apiSecret=self.cloud_config.api_secret,
                apiDeviceID=None,
            )
        except Exception as exc:
            raise RuntimeError(f"Failed to query Tuya Cloud: {exc}") from exc

        if not client.token:
            raise RuntimeError(self._format_cloud_error(client.error))

        return client

    def _load_cloud_device(self, device_id: str) -> dict[str, Any]:
        client = self._create_cloud_client()
        payload = client.cloudrequest(f"/v2.0/cloud/thing/{device_id}")
        if not isinstance(payload, dict):
            raise RuntimeError(f"Unexpected Tuya Cloud payload: {type(payload).__name__}")

        if not payload.get("success"):
            raise RuntimeError(self._format_cloud_error(payload))

        result = payload.get("result")
        if not isinstance(result, dict):
            raise RuntimeError("Tuya Cloud did not return a device object.")

        return result

    def _enrich_discovery_with_cloud_keys(self, discovered: dict[str, Any]) -> dict[str, Any]:
        if not discovered or self.cloud_config is None:
            return discovered

        enriched: dict[str, Any] = {}
        resolved_any = False
        errors: list[str] = []
        for key, details in discovered.items():
            if not isinstance(details, dict):
                continue

            copied = dict(details)
            existing_local_key = str(
                copied.get("local_key")
                or copied.get("localKey")
                or copied.get("key")
                or ""
            ).strip()
            if existing_local_key:
                enriched[str(key)] = copied
                continue

            device_id = str(
                copied.get("gwId")
                or copied.get("id")
                or copied.get("device_id")
                or ""
            ).strip()
            if not device_id:
                enriched[str(key)] = copied
                continue

            try:
                cloud_item = self._load_cloud_device(device_id)
            except Exception as exc:
                message = f"{device_id}: {exc}"
                errors.append(message)
                LOGGER.warning("Tuya Cloud lookup failed for %s: %s", device_id, exc)
                enriched[str(key)] = copied
                continue

            cloud_key = str(
                cloud_item.get("local_key")
                or cloud_item.get("key")
                or ""
            ).strip()
            if cloud_key:
                copied["local_key"] = cloud_key
                copied["localKeySource"] = "cloud"
                resolved_any = True

            enriched[str(key)] = copied

        self.last_cloud_lookup_at = datetime.now(timezone.utc).isoformat()
        self.last_cloud_lookup_error = None if not errors else " | ".join(errors[:3])
        if not resolved_any:
            LOGGER.info("Tuya Cloud lookup completed but did not match any local keys.")
        return enriched

    def _scan_via_task_helper(self) -> dict[str, Any]:
        request_id = uuid.uuid4().hex
        request_payload = {
            "requestId": request_id,
            "scanTimeout": self.scan_timeout,
            "requestedAt": datetime.now(timezone.utc).isoformat(),
        }

        self.discovery_request_path.parent.mkdir(parents=True, exist_ok=True)
        self.discovery_result_path.parent.mkdir(parents=True, exist_ok=True)
        self.discovery_request_path.write_text(json.dumps(request_payload, indent=2, sort_keys=True))
        if self.discovery_result_path.exists():
            self.discovery_result_path.unlink()

        LOGGER.info("Starting discovery helper task %s", self.discovery_task_name)
        result = subprocess.run(
            ["schtasks.exe", "/run", "/tn", self.discovery_task_name],
            capture_output=True,
            text=True,
            timeout=15,
            check=False,
        )
        if result.returncode != 0:
            detail = (result.stderr or result.stdout or "").strip() or f"exit code {result.returncode}"
            raise RuntimeError(
                f"Failed to start discovery helper task '{self.discovery_task_name}': {detail}"
            )

        deadline = time.monotonic() + self.discovery_wait_timeout
        while time.monotonic() < deadline:
            if self.discovery_result_path.exists():
                try:
                    payload = json.loads(self.discovery_result_path.read_text())
                except json.JSONDecodeError:
                    time.sleep(0.25)
                    continue

                if str(payload.get("requestId", "")).strip() != request_id:
                    time.sleep(0.25)
                    continue

                error = str(payload.get("error", "")).strip()
                if error:
                    raise RuntimeError(f"Discovery helper failed: {error}")

                discovered = payload.get("discovered", {})
                if not isinstance(discovered, dict):
                    raise RuntimeError("Discovery helper returned an unexpected payload.")
                return discovered

            time.sleep(0.25)

        raise RuntimeError(
            f"Timed out waiting for discovery helper task '{self.discovery_task_name}'. "
            "Make sure the DeyeHomeBridgeDiscovery task is installed and the user is logged in."
        )

    def refresh_device_ips(self) -> None:
        if not self.devices:
            self.last_scan_results = []
            self.last_scan_at = datetime.now(timezone.utc).isoformat()
            self.save_state_cache()
            return

        LOGGER.info("Running TinyTuya discovery for %s configured device(s)", len(self.devices))

        try:
            discovered = self._scan_raw()
        except Exception as exc:
            LOGGER.warning("TinyTuya discovery failed: %s", exc)
            return

        discovered = self._enrich_discovery_with_cloud_keys(discovered)

        known_ids = set(self.devices.keys())
        updated = 0

        for key, details in discovered.items():
            normalized = self._normalize_scan_result(key, details)
            device_id = normalized["device_id"]
            if not device_id or device_id not in known_ids:
                continue

            ip = normalized["ip"]
            if not ip:
                continue

            self.devices[device_id].ip = ip
            updated += 1

        self.last_scan_results = self._normalize_scan_results(discovered)
        self.last_scan_at = datetime.now(timezone.utc).isoformat()
        self.save_state_cache()
        LOGGER.info("Discovery refreshed IPs for %s device(s)", updated)

    def scan_network(self) -> list[dict[str, Any]]:
        LOGGER.info("Running admin discovery scan")
        discovered = self._scan_raw()
        discovered = self._enrich_discovery_with_cloud_keys(discovered)
        results = self._normalize_scan_results(discovered)
        self.last_scan_results = results
        self.last_scan_at = datetime.now(timezone.utc).isoformat()
        return results

    def _normalize_scan_results(self, discovered: dict[str, Any]) -> list[dict[str, Any]]:
        results = [
            self._normalize_scan_result(key, details)
            for key, details in discovered.items()
            if isinstance(details, dict)
        ]
        return sorted(
            results,
            key=lambda item: (
                not item["configured"],
                item["name"].lower(),
                item["device_id"],
            ),
        )

    def _normalize_scan_result(self, key: Any, details: dict[str, Any]) -> dict[str, Any]:
        device_id = str(
            details.get("gwId")
            or details.get("id")
            or details.get("device_id")
            or ""
        ).strip()
        ip = str(details.get("ip") or details.get("ip_address") or key or "").strip()
        version = str(details.get("version") or details.get("ver") or "3.3").strip() or "3.3"
        name = str(
            details.get("name")
            or details.get("product_name")
            or details.get("productName")
            or details.get("gwId")
            or ip
        ).strip()
        category = str(details.get("category") or "").strip()
        product_key = str(details.get("productKey") or details.get("product_key") or "").strip()
        discovered_local_key = str(
            details.get("local_key")
            or details.get("localKey")
            or details.get("key")
            or ""
        ).strip()

        existing = self.devices.get(device_id)
        effective_local_key = existing.local_key if existing else discovered_local_key
        local_key_source = "configured" if existing and existing.local_key else ""
        if not local_key_source and discovered_local_key:
            local_key_source = str(details.get("localKeySource") or "cloud").strip() or "cloud"
        return {
            "device_id": device_id,
            "ip": ip,
            "name": existing.name if existing else name,
            "category": existing.category if existing and existing.category else category,
            "protocol_version": existing.protocol_version if existing else version,
            "product_key": product_key,
            "configured": existing is not None,
            "local_key": effective_local_key,
            "has_local_key": bool(effective_local_key),
            "local_key_source": local_key_source,
        }

    async def _run_loop(self) -> None:
        headers = {"Authorization": f"Bearer {self.bridge_token}"}
        timeout = httpx.Timeout(self.request_timeout)

        async with httpx.AsyncClient(base_url=self.cluster_base_url, timeout=timeout, headers=headers) as client:
            while not self._stop_event.is_set():
                try:
                    await self._sync_once(client)
                    self.last_sync_at = datetime.now(timezone.utc).isoformat()
                    self.last_error = None
                except Exception as exc:
                    self.last_error = str(exc)
                    LOGGER.exception("Bridge sync failed")

                try:
                    await asyncio.wait_for(self._stop_event.wait(), timeout=self.poll_interval)
                except asyncio.TimeoutError:
                    continue

    async def _sync_once(self, client: httpx.AsyncClient) -> None:
        device_reports = await asyncio.to_thread(self.collect_device_reports)
        completed_snapshot = list(self.completed_commands)
        payload = {
            "bridgeId": self.bridge_id,
            "heartbeat": {
                "bridgeVersion": BRIDGE_VERSION,
                "hostName": socket.gethostname(),
            },
            "devices": device_reports,
            "completedCommands": completed_snapshot,
        }

        response = await client.post("/api/bridge/sync", json=payload)
        response.raise_for_status()

        if completed_snapshot:
            self.completed_commands = self.completed_commands[len(completed_snapshot):]

        pending_commands = response.json().get("pendingCommands", [])
        if pending_commands:
            results = await asyncio.to_thread(self.execute_commands, pending_commands)
            self.completed_commands.extend(results)

    def collect_device_reports(self) -> list[dict[str, Any]]:
        reports: list[dict[str, Any]] = []
        for device in sorted(list(self.devices.values()), key=lambda item: item.name.lower()):
            reports.append(self.read_device_report(device))
        return reports

    def execute_commands(self, commands: list[dict[str, Any]]) -> list[dict[str, Any]]:
        results: list[dict[str, Any]] = []
        for command in commands:
            command_id = command.get("commandId")
            command_type = str(command.get("commandType", "")).strip()
            device_id = str(command.get("deviceId", "")).strip()
            desired_state = command.get("desiredState")

            try:
                if command_type == "set_state":
                    config = self.devices.get(device_id)
                    if config is None:
                        raise RuntimeError(f"Device {device_id} is not configured on the bridge.")
                    self.set_device_state(config, bool(desired_state))
                    message = "Device state updated."
                elif command_type == "refresh_inventory":
                    message = "Inventory refresh completed."
                else:
                    raise RuntimeError(f"Unsupported command type: {command_type}")

                results.append({
                    "commandId": command_id,
                    "success": True,
                    "message": message,
                })
            except Exception as exc:
                LOGGER.warning("Command %s failed: %s", command_id, exc)
                results.append({
                    "commandId": command_id,
                    "success": False,
                    "message": str(exc),
                })

        return results

    def read_device_report(self, config: DeviceConfig) -> dict[str, Any]:
        try:
            device = self.create_device(config)
            status = device.status()
            if not isinstance(status, dict):
                raise RuntimeError("Unexpected TinyTuya status payload.")
            if status.get("Error"):
                raise RuntimeError(str(status["Error"]))

            dps = status.get("dps", {})
            return {
                "deviceId": config.device_id,
                "name": config.name,
                "category": config.category,
                "online": True,
                "isOn": self.extract_is_on(dps),
                "currentPowerW": self.extract_power_w(dps),
                "error": None,
            }
        except Exception as exc:
            return {
                "deviceId": config.device_id,
                "name": config.name,
                "category": config.category,
                "online": False,
                "isOn": False,
                "currentPowerW": None,
                "error": str(exc),
            }

    def set_device_state(self, config: DeviceConfig, desired_state: bool) -> None:
        device = self.create_device(config)
        result = device.turn_on() if desired_state else device.turn_off()
        if isinstance(result, dict) and result.get("Error"):
            raise RuntimeError(str(result["Error"]))

    def create_device(self, config: DeviceConfig) -> Any:
        device = tinytuya.OutletDevice(
            config.device_id,
            config.ip or "Auto",
            config.local_key,
        )
        device.set_version(float(config.protocol_version))
        try:
            device.set_socketPersistent(True)
        except Exception:
            pass
        return device

    def admin_state(self) -> dict[str, Any]:
        return {
            "bridgeId": self.bridge_id,
            "bridgeVersion": BRIDGE_VERSION,
            "clusterBaseUrl": self.cluster_base_url,
            "configPath": str(self.config_path),
            "statePath": str(self.state_path),
            "cloudConfig": self.cloud_state(),
            "discoveryMode": self.discovery_mode,
            "lastSyncAt": self.last_sync_at,
            "lastError": self.last_error,
            "lastScanAt": self.last_scan_at,
            "discoveryHint": DISCOVERY_HINT,
            "devices": [
                config.to_dict()
                for config in sorted(self.devices.values(), key=lambda item: item.name.lower())
            ],
            "lastScanResults": self.last_scan_results,
        }

    @staticmethod
    def extract_is_on(dps: dict[str, Any]) -> bool:
        for key in ("switch_1", "1"):
            value = dps.get(key)
            if isinstance(value, bool):
                return value

        for value in dps.values():
            if isinstance(value, bool):
                return value

        return False

    @staticmethod
    def extract_power_w(dps: dict[str, Any]) -> int | None:
        candidates = (
            ("cur_power", 0.1),
            ("19", 0.1),
            ("20", 0.1),
            ("18", 1.0),
            ("5", 1.0),
            ("6", 1.0),
        )

        for key, scale in candidates:
            raw = dps.get(key)
            if isinstance(raw, (int, float)):
                return int(round(float(raw) * scale))

        for key, raw in dps.items():
            if "power" in str(key).lower() and isinstance(raw, (int, float)):
                if float(raw) > 200:
                    return int(round(float(raw) / 10.0))
                return int(round(float(raw)))

        return None


logging.basicConfig(
    level=os.getenv("DEYE_BRIDGE_LOG_LEVEL", "INFO").upper(),
    format="%(asctime)s %(levelname)s %(name)s %(message)s",
)

runtime = BridgeRuntime()
app = FastAPI(title="Deye Home Bridge")


@app.on_event("startup")
async def on_startup() -> None:
    await runtime.start()


@app.on_event("shutdown")
async def on_shutdown() -> None:
    await runtime.stop()


@app.get("/", response_class=HTMLResponse)
async def admin_page() -> HTMLResponse:
    return HTMLResponse(ADMIN_UI_PATH.read_text())


@app.get("/api/admin/status")
async def admin_status() -> dict[str, Any]:
    return runtime.admin_state()


@app.post("/api/admin/scan")
async def admin_scan() -> dict[str, Any]:
    try:
        results = await asyncio.to_thread(runtime.scan_network)
    except Exception as exc:
        raise HTTPException(status_code=500, detail=str(exc)) from exc

    return {
        "lastScanAt": runtime.last_scan_at,
        "devices": results,
        "discoveryHint": DISCOVERY_HINT,
        "cloudConfig": runtime.cloud_state(),
    }


@app.post("/api/admin/cloud-config")
async def admin_save_cloud_config(payload: dict[str, Any]) -> dict[str, Any]:
    try:
        config = CloudKeyConfig.from_dict(payload)
    except ValueError as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc

    try:
        await asyncio.to_thread(runtime.save_cloud_config, config if config.is_configured() else None)
    except Exception as exc:
        raise HTTPException(status_code=500, detail=str(exc)) from exc

    return runtime.admin_state()


@app.post("/api/admin/config")
async def admin_save_config(payload: dict[str, Any]) -> dict[str, Any]:
    raw_devices = payload.get("devices", [])
    if not isinstance(raw_devices, list):
        raise HTTPException(status_code=400, detail="'devices' must be a list.")

    try:
        devices = [DeviceConfig.from_dict(item) for item in raw_devices if isinstance(item, dict)]
    except ValueError as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc

    try:
        await asyncio.to_thread(runtime.replace_devices, devices, True)
    except Exception as exc:
        raise HTTPException(status_code=500, detail=str(exc)) from exc

    return runtime.admin_state()


@app.get("/healthz")
async def healthz() -> dict[str, Any]:
    return {
        "status": "ok" if runtime.last_error is None else "degraded",
        "bridgeId": runtime.bridge_id,
        "deviceCount": len(runtime.devices),
        "lastSyncAt": runtime.last_sync_at,
        "lastScanAt": runtime.last_scan_at,
        "lastError": runtime.last_error,
        "version": BRIDGE_VERSION,
    }
