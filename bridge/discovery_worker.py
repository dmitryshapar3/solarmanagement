import json
import os
import traceback
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import tinytuya


BASE_DIR = Path(__file__).resolve().parent
REQUEST_PATH = Path(os.getenv("DEYE_BRIDGE_DISCOVERY_REQUEST_PATH", str(BASE_DIR / "discovery-request.json")))
RESULT_PATH = Path(os.getenv("DEYE_BRIDGE_DISCOVERY_RESULT_PATH", str(BASE_DIR / "discovery-result.json")))


def read_request() -> dict[str, Any]:
    if not REQUEST_PATH.exists():
        return {}

    try:
        payload = json.loads(REQUEST_PATH.read_text())
    except json.JSONDecodeError:
        return {}

    return payload if isinstance(payload, dict) else {}


def write_result(payload: dict[str, Any]) -> None:
    RESULT_PATH.parent.mkdir(parents=True, exist_ok=True)
    RESULT_PATH.write_text(json.dumps(payload, indent=2, sort_keys=True))


def main() -> None:
    request = read_request()
    request_id = str(request.get("requestId", "")).strip()
    scan_timeout = max(1, int(float(request.get("scanTimeout", 15))))

    result_payload: dict[str, Any] = {
        "requestId": request_id,
        "scannedAt": datetime.now(timezone.utc).isoformat(),
        "discovered": {},
        "error": "",
    }

    try:
        discovered = tinytuya.deviceScan(False, scan_timeout, True, False)
        if not isinstance(discovered, dict):
            raise RuntimeError("TinyTuya discovery returned an unexpected payload.")
        result_payload["discovered"] = discovered
    except Exception:
        result_payload["error"] = traceback.format_exc()

    write_result(result_payload)


if __name__ == "__main__":
    main()
