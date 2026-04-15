"""
Local Key Hook for Bridge

This module provides a hook function to resolve local keys for discovered devices
by intercepting network traffic and searching for 'local_key' values in JSON objects.

The scan flow:
1. LAN discovery finds devices
2. The bridge calls local_key_hook.py once per discovered device with its IP
3. It waits up to local_key_hook_timeout seconds
4. If the hook returns a key, the result is tagged as "local key from hook"
5. If the hook returns nothing, Cloud Key Assist still gets a chance afterward
"""

import json
import logging
import socket
import threading
import time
from dataclasses import dataclass
from typing import Any, Optional

try:
    from scapy.all import sniff, TCP, IP, Raw, conf
    conf.verb = 0  # Disable scapy verbose output
except ImportError:
    sniff = None  # type: ignore
    TCP = None  # type: ignore
    IP = None  # type: ignore
    Raw = None  # type: ignore


LOGGER = logging.getLogger(__name__)


@dataclass
class LocalKeyRequest:
    """Request object for resolving a local key."""
    ip: str
    device_id: str
    name: str
    category: Optional[str] = None
    product_key: Optional[str] = None
    protocol_version: str = "3.3"
    raw_scan_result: Optional[dict[str, Any]] = None


@dataclass
class LocalKeyResult:
    """Result object containing the resolved local key and source."""
    local_key: str
    source: str


def _extract_json_from_data(data: bytes) -> list[Any]:
    """
    Extract JSON objects from raw data.

    Args:
        data: Raw bytes data

    Returns:
        List of extracted JSON objects
    """
    json_objects = []

    try:
        text = data.decode('utf-8', errors='ignore')

        # Find potential JSON objects by looking for braces/brackets
        start_idx = 0
        while start_idx < len(text):
            for start_char in ['{', '[']:
                start_pos = text.find(start_char, start_idx)
                if start_pos == -1:
                    continue

                # Try to parse JSON starting from this position
                for end_pos in range(start_pos + 1, min(len(text) + 1, start_pos + 10000)):
                    try:
                        candidate = text[start_pos:end_pos]
                        parsed = json.loads(candidate)
                        json_objects.append(parsed)
                        start_idx = end_pos
                        break
                    except (json.JSONDecodeError, ValueError):
                        continue
                else:
                    continue
                break
            else:
                start_idx += 1

    except Exception:
        pass

    return json_objects


def _search_for_local_key(data: Any, path: str = "") -> list[tuple[str, Any]]:
    """
    Recursively search for 'local_key' in JSON structure.

    Args:
        data: JSON object (dict or list)
        path: Current path in the JSON structure

    Returns:
        List of tuples (path, value) where local_key was found
    """
    results = []

    if isinstance(data, dict):
        for key, value in data.items():
            current_path = f"{path}.{key}" if path else key

            if key == "local_key":
                results.append((current_path, value))

            # Recurse into nested structures
            if isinstance(value, (dict, list)):
                results.extend(_search_for_local_key(value, current_path))

    elif isinstance(data, list):
        for idx, item in enumerate(data):
            current_path = f"{path}[{idx}]"

            if isinstance(item, (dict, list)):
                results.extend(_search_for_local_key(item, current_path))

    return results


def _packet_callback(packet, target_ip: str, results: list, lock: threading.Lock):
    """
    Callback function for each captured packet.

    Args:
        packet: Scapy packet object
        target_ip: The target IP address being monitored
        results: List to store found local keys
        lock: Thread lock for thread-safe access to results
    """
    if IP is None or TCP is None or Raw is None:
        return

    # Check if packet has the required layers
    if not (IP in packet and TCP in packet and Raw in packet):
        return

    # Check if packet is from or to target IP
    ip_layer = packet[IP]
    if ip_layer.src != target_ip and ip_layer.dst != target_ip:
        return

    # Extract payload
    payload = packet[Raw].load

    # Try to extract and search JSON
    json_objects = _extract_json_from_data(payload)

    for json_obj in json_objects:
        matches = _search_for_local_key(json_obj)

        if matches:
            for path, value in matches:
                if isinstance(value, str) and len(value) >= 8:  # Typical local_key length
                    direction = "RECEIVED" if ip_layer.src == target_ip else "SENT"
                    with lock:
                        results.append({
                            'local_key': value,
                            'source': f"traffic_intercept_{direction.lower()}",
                            'path': path,
                            'timestamp': time.time()
                        })


def _sniff_for_local_key(target_ip: str, timeout: float) -> Optional[str]:
    """
    Sniff network traffic for a specific IP to find local_key values.

    Args:
        target_ip: The IP address to monitor
        timeout: Maximum time to wait for packets

    Returns:
        The found local_key or None if not found
    """
    if sniff is None:
        LOGGER.warning("scapy is not installed, cannot intercept traffic")
        return None

    results: list[dict[str, Any]] = []
    lock = threading.Lock()

    # Build BPF filter
    bpf_filter = f"host {target_ip}"

    LOGGER.debug(f"Starting traffic interception for IP: {target_ip}, timeout: {timeout}s")

    try:
        sniff(
            filter=bpf_filter,
            prn=lambda pkt: _packet_callback(pkt, target_ip, results, lock),
            timeout=timeout,
            store=False
        )
    except Exception as exc:
        LOGGER.warning(f"Traffic interception failed for {target_ip}: {exc}")
        return None

    if results:
        # Return the first valid local_key found
        local_key = results[0]['local_key']
        LOGGER.info(f"Found local_key for {target_ip} via traffic interception")
        return local_key

    LOGGER.debug(f"No local_key found for {target_ip} within timeout")
    return None


def resolve_local_key(request: LocalKeyRequest) -> Optional[str | LocalKeyResult]:
    """
    Resolve the local key for a discovered device.

    This function intercepts network traffic from the specified IP address and searches
    for 'local_key' values in JSON objects being transmitted. It's called by the bridge
    once per discovered device during the scan flow.

    Args:
        request: LocalKeyRequest containing device information including:
            - ip: The IP address of the discovered device
            - device_id: The device identifier
            - name: Device name
            - category: Device category (optional)
            - product_key: Product key (optional)
            - protocol_version: Protocol version (default: "3.3")
            - raw_scan_result: Raw scan result data (optional)

    Returns:
        - None if no key could be resolved (Cloud Key Assist will try afterward)
        - str if the local key was found
        - LocalKeyResult if the local key was found with a custom source label

    Example:
        >>> request = LocalKeyRequest(ip="192.168.1.100", device_id="abc123", name="My Device")
        >>> result = resolve_local_key(request)
        >>> if result:
        ...     print(f"Found key: {result}")
    """
    target_ip = request.ip

    if not target_ip:
        LOGGER.debug(f"No IP provided for device {request.device_id}")
        return None

    # Validate IP address format
    try:
        socket.inet_aton(target_ip)
    except socket.error:
        LOGGER.warning(f"Invalid IP address format: {target_ip}")
        return None

    LOGGER.info(f"Attempting to resolve local_key for device {request.device_id} ({request.name}) at {target_ip}")

    # Default timeout for traffic interception (can be configured via environment)
    import os
    timeout = float(os.environ.get("DEYE_BRIDGE_LOCAL_KEY_TIMEOUT", "3.0"))

    # Attempt to intercept traffic and find local_key
    local_key = _sniff_for_local_key(target_ip, timeout)

    if local_key:
        # Return with custom source label for UI display
        return LocalKeyResult(
            local_key=local_key,
            source="local key from hook"
        )

    LOGGER.debug(f"No local_key resolved for device {request.device_id} at {target_ip}")
    return None


# Convenience function for direct usage without dataclass
def resolve_local_key_simple(
    ip: str,
    device_id: str,
    name: str,
    category: Optional[str] = None,
    product_key: Optional[str] = None,
    protocol_version: str = "3.3",
    raw_scan_result: Optional[dict[str, Any]] = None
) -> Optional[str | LocalKeyResult]:
    """
    Simple wrapper for resolve_local_key that accepts individual parameters.

    Args:
        ip: The IP address of the discovered device
        device_id: The device identifier
        name: Device name
        category: Device category (optional)
        product_key: Product key (optional)
        protocol_version: Protocol version (default: "3.3")
        raw_scan_result: Raw scan result data (optional)

    Returns:
        Same as resolve_local_key
    """
    request = LocalKeyRequest(
        ip=ip,
        device_id=device_id,
        name=name,
        category=category,
        product_key=product_key,
        protocol_version=protocol_version,
        raw_scan_result=raw_scan_result
    )
    return resolve_local_key(request)


if __name__ == "__main__":
    # Example usage / testing
    logging.basicConfig(level=logging.INFO)

    # Create a test request
    test_request = LocalKeyRequest(
        ip="192.168.1.100",
        device_id="test_device_123",
        name="Test Device",
        category="switch"
    )

    print(f"Testing local_key resolution for {test_request.ip}...")
    result = resolve_local_key(test_request)

    if result is None:
        print("No local_key found (Cloud Key Assist may try afterward)")
    elif isinstance(result, LocalKeyResult):
        print(f"Found local_key: {result.local_key}")
        print(f"Source: {result.source}")
    else:
        print(f"Found local_key: {result}")
