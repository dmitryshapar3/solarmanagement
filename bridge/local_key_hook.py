from __future__ import annotations

from dataclasses import dataclass
from typing import Any


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


def resolve_local_key(request: LocalKeyRequest) -> str | LocalKeyResult | None:
    """
    Return a local key for a discovered device, or None if this hook cannot supply one.

    Contract:
    - Input: one discovered device at a time, including its IP and scan metadata.
    - Output:
      - None: no key available for this device
      - str: a resolved local key
      - LocalKeyResult: a resolved local key plus a short source label
    - Blocking is allowed. The bridge waits up to DEYE_BRIDGE_LOCAL_KEY_HOOK_TIMEOUT seconds.
    - For normal "not found" cases, return None instead of raising.
    - Raise only for real failures you want surfaced in the bridge UI/log.

    File to edit:
    - bridge/local_key_hook.py
    """

    # Example:
    # if request.ip == "192.168.31.97":
    #     return LocalKeyResult(local_key="paste-real-key-here", source="lab-hook")

    return None
