from __future__ import annotations

import json
import logging
import os
import re
from dataclasses import dataclass
from datetime import datetime
from typing import Any

# Default log file path - can be overridden via environment variable
DEFAULT_LOG_FILE = "/tmp/local_key_interceptor.log"
LOG_FILE_PATH = (
    os.environ.get("DEYE_BRIDGE_LOCAL_KEY_HOOK_LOG_PATH")
    or os.environ.get("DEYE_BRIDGE_LOCAL_KEY_LOG_FILE")
    or DEFAULT_LOG_FILE
)

LOGGER = logging.getLogger(__name__)


def _log_to_file(message: str, level: str = "INFO", clear_first: bool = False) -> None:
    """Log a message to the updatable log file."""
    timestamp = datetime.now().isoformat()
    log_entry = f"[{timestamp}] [{level}] {message}\n"
    try:
        mode = "w" if clear_first else "a"
        with open(LOG_FILE_PATH, mode, encoding="utf-8") as f:
            f.write(log_entry)
    except Exception as e:
        LOGGER.warning(f"Failed to write to log file {LOG_FILE_PATH}: {e}")


@dataclass(slots=True)
class LocalKeyRequest:
    ip: str
    device_id: str
    name: str
    category: str
    product_key: str
    protocol_version: str
    raw_scan_result: dict[str, Any]


@dataclass(slots=True)
class LocalKeyResult:
    local_key: str
    source: str = "hook"


def _is_valid_ip(ip: str) -> bool:
    """Validate IPv4 address format."""
    pattern = r"^(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)$"
    return bool(re.match(pattern, ip.strip()))


def _extract_local_key_from_json(data: Any) -> str | None:
    """Recursively search for 'local_key' field in JSON structure."""
    if isinstance(data, dict):
        for key, value in data.items():
            if key == "local_key" and isinstance(value, str) and value.strip():
                return value.strip()
            result = _extract_local_key_from_json(value)
            if result:
                return result
    elif isinstance(data, list):
        for item in data:
            result = _extract_local_key_from_json(item)
            if result:
                return result
    return None


def _parse_json_from_payload(payload: bytes) -> Any | None:
    """Try to parse JSON from raw payload bytes."""
    try:
        return json.loads(payload.decode("utf-8", errors="ignore"))
    except (json.JSONDecodeError, UnicodeDecodeError):
        pass

    try:
        text = payload.decode("utf-8", errors="ignore")
        start = text.find("{")
        if start != -1:
            depth = 0
            for i, char in enumerate(text[start:], start):
                if char == "{":
                    depth += 1
                elif char == "}":
                    depth -= 1
                    if depth == 0:
                        candidate = text[start : i + 1]
                        try:
                            return json.loads(candidate)
                        except json.JSONDecodeError:
                            pass
    except Exception:
        pass

    return None


def resolve_local_key(request: LocalKeyRequest) -> str | LocalKeyResult | None:
    """
    Intercept local network traffic to find a local_key hidden within JSON payloads.

    This function uses scapy to sniff network packets to/from the target device IP,
    inspects TCP payloads for valid JSON objects, and recursively searches for
    a field named 'local_key'.
    """
    timeout = float(
        os.environ.get("DEYE_BRIDGE_LOCAL_KEY_HOOK_TIMEOUT")
        or os.environ.get("DEYE_BRIDGE_LOCAL_KEY_TIMEOUT")
        or "60"
    )

    if not request.ip or not _is_valid_ip(request.ip):
        _log_to_file(
            f"Invalid or missing IP address: {request.ip} (device_id={request.device_id}, name={request.name})",
            "WARNING",
        )
        LOGGER.warning("Invalid or missing IP address: %s", request.ip)
        return None

    target_ip = request.ip.strip()
    found_key: str | None = None
    stop_event: list[bool] = [False]

    _log_to_file("=== Starting new local_key interception session ===", "INFO", clear_first=True)
    _log_to_file(
        f"Device: {request.device_id} ({request.name}), IP: {target_ip}, Timeout: {timeout}s",
        "INFO",
    )

    try:
        from scapy.all import IP, TCP, sniff
    except ImportError:
        _log_to_file(
            f"scapy library not available; cannot intercept traffic for device {request.device_id}",
            "WARNING",
        )
        LOGGER.warning("scapy library not available; cannot intercept traffic.")
        return None
    except Exception as exc:
        _log_to_file(f"Failed to import scapy for device {request.device_id}: {exc}", "WARNING")
        LOGGER.warning("Failed to import scapy: %s", exc)
        return None

    def packet_callback(packet: Any) -> None:
        nonlocal found_key

        if stop_event[0] or found_key:
            return

        if not packet.haslayer(IP):
            return

        ip_layer = packet[IP]
        if ip_layer.src != target_ip and ip_layer.dst != target_ip:
            return

        if not packet.haslayer(TCP):
            return

        tcp_layer = packet[TCP]
        if not tcp_layer.payload:
            return

        try:
            raw_payload = bytes(tcp_layer.payload)
        except Exception:
            return

        if not raw_payload:
            return

        json_data = _parse_json_from_payload(raw_payload)
        if json_data is None:
            return

        extracted_key = _extract_local_key_from_json(json_data)
        if extracted_key:
            found_key = extracted_key
            stop_event[0] = True
            _log_to_file(
                f"Found local_key for device {request.device_id} ({request.name}) at {target_ip}: {found_key[:8]}..."
            )
            LOGGER.info("Found local_key for device %s at %s", request.device_id, target_ip)

    try:
        bpf_filter = f"host {target_ip}"
        _log_to_file(f"Starting packet sniff with BPF filter: {bpf_filter}, timeout: {timeout}s")
        LOGGER.info(
            "Starting packet sniff for device %s at %s with timeout %ds",
            request.device_id,
            target_ip,
            timeout,
        )
        sniff(
            filter=bpf_filter,
            prn=packet_callback,
            timeout=timeout,
            store=False,
            stop_filter=lambda x: stop_event[0],
        )
    except PermissionError:
        _log_to_file(
            f"Permission denied: scapy requires root privileges for device {request.device_id}",
            "WARNING",
        )
        LOGGER.warning("Permission denied: scapy requires root privileges for packet sniffing.")
        return None
    except OSError as exc:
        _log_to_file(f"OS error during packet capture for device {request.device_id}: {exc}", "WARNING")
        LOGGER.warning("OS error during packet capture: %s", exc)
        return None
    except Exception as exc:
        _log_to_file(
            f"Unexpected error during packet capture for device {request.device_id}: {exc}",
            "WARNING",
        )
        LOGGER.warning("Unexpected error during packet capture: %s", exc)
        return None

    if found_key:
        _log_to_file(
            f"Returning local_key for device {request.device_id}: {found_key[:8]}... (source: local key from hook)"
        )
        return LocalKeyResult(local_key=found_key, source="local key from hook")

    _log_to_file(
        f"No local_key found for device {request.device_id} ({request.name}) at {target_ip} within {timeout}s timeout",
        "INFO",
    )
    LOGGER.info("No local_key found for device %s at %s within %ds timeout", request.device_id, target_ip, timeout)
    return None
