import asyncio
import contextlib
import json
import logging
import os
import sys
from pathlib import Path
from typing import Any

import servicemanager
import uvicorn
import win32service
import win32serviceutil


BASE_DIR = Path(__file__).resolve().parent
SERVICE_CONFIG_PATH = BASE_DIR / "service-config.json"


class ServiceSafeUvicornServer(uvicorn.Server):
    def capture_signals(self) -> contextlib.AbstractContextManager[None]:
        return contextlib.nullcontext()


def resolve_path(value: str) -> Path:
    path = Path(value)
    if not path.is_absolute():
        path = BASE_DIR / path
    return path.resolve()


def load_service_settings() -> dict[str, Any]:
    if not SERVICE_CONFIG_PATH.exists():
        raise RuntimeError(
            f"Missing {SERVICE_CONFIG_PATH.name}. Copy service-config.example.json and fill in the bridge token first."
        )

    try:
        payload = json.loads(SERVICE_CONFIG_PATH.read_text())
    except json.JSONDecodeError as exc:
        raise RuntimeError(f"Invalid {SERVICE_CONFIG_PATH.name}: {exc}") from exc

    if not isinstance(payload, dict):
        raise RuntimeError(f"{SERVICE_CONFIG_PATH.name} must be a JSON object.")

    settings = {
        "cluster_base_url": str(payload.get("cluster_base_url", "")).strip(),
        "bridge_id": str(payload.get("bridge_id", "home-main")).strip() or "home-main",
        "bridge_token": str(payload.get("bridge_token", "")).strip(),
        "bridge_config_path": str(payload.get("bridge_config_path", "bridge-config.json")).strip() or "bridge-config.json",
        "bridge_state_path": str(payload.get("bridge_state_path", "bridge-state.json")).strip() or "bridge-state.json",
        "cloud_config_path": str(payload.get("cloud_config_path", "cloud-config.json")).strip() or "cloud-config.json",
        "discovery_mode": str(payload.get("discovery_mode", "task-helper")).strip().lower() or "task-helper",
        "discovery_task_name": str(payload.get("discovery_task_name", "DeyeHomeBridgeDiscovery")).strip() or "DeyeHomeBridgeDiscovery",
        "discovery_request_path": str(payload.get("discovery_request_path", "discovery-request.json")).strip() or "discovery-request.json",
        "discovery_result_path": str(payload.get("discovery_result_path", "discovery-result.json")).strip() or "discovery-result.json",
        "discovery_wait_timeout": float(payload.get("discovery_wait_timeout", 25)),
        "bind_host": str(payload.get("bind_host", "127.0.0.1")).strip() or "127.0.0.1",
        "port": int(payload.get("port", 8081)),
        "poll_interval": float(payload.get("poll_interval", 2)),
        "request_timeout": float(payload.get("request_timeout", 10)),
        "scan_timeout": int(payload.get("scan_timeout", 15)),
        "log_level": str(payload.get("log_level", "info")).strip().lower() or "info",
        "log_path": str(payload.get("log_path", "bridge-service.log")).strip() or "bridge-service.log",
    }

    if not settings["cluster_base_url"]:
        raise RuntimeError("service-config.json is missing cluster_base_url.")
    if not settings["bridge_token"]:
        raise RuntimeError("service-config.json is missing bridge_token.")

    settings["bridge_config_path"] = str(resolve_path(settings["bridge_config_path"]))
    settings["bridge_state_path"] = str(resolve_path(settings["bridge_state_path"]))
    settings["cloud_config_path"] = str(resolve_path(settings["cloud_config_path"]))
    settings["discovery_request_path"] = str(resolve_path(settings["discovery_request_path"]))
    settings["discovery_result_path"] = str(resolve_path(settings["discovery_result_path"]))
    settings["log_path"] = str(resolve_path(settings["log_path"]))
    return settings


def configure_logging(settings: dict[str, Any], include_console: bool) -> None:
    log_path = Path(settings["log_path"])
    log_path.parent.mkdir(parents=True, exist_ok=True)
    handlers: list[logging.Handler] = [logging.FileHandler(log_path, encoding="utf-8")]
    if include_console:
        handlers.append(logging.StreamHandler(sys.stdout))

    logging.basicConfig(
        level=getattr(logging, str(settings["log_level"]).upper(), logging.INFO),
        format="%(asctime)s %(levelname)s %(name)s %(message)s",
        handlers=handlers,
        force=True,
    )


def apply_environment(settings: dict[str, Any]) -> None:
    env_map = {
        "cluster_base_url": "DEYE_CLUSTER_BASE_URL",
        "bridge_id": "DEYE_BRIDGE_ID",
        "bridge_token": "DEYE_BRIDGE_TOKEN",
        "bridge_config_path": "DEYE_BRIDGE_CONFIG",
        "bridge_state_path": "DEYE_BRIDGE_STATE",
        "cloud_config_path": "DEYE_BRIDGE_CLOUD_CONFIG",
        "discovery_mode": "DEYE_BRIDGE_DISCOVERY_MODE",
        "discovery_task_name": "DEYE_BRIDGE_DISCOVERY_TASK_NAME",
        "discovery_request_path": "DEYE_BRIDGE_DISCOVERY_REQUEST_PATH",
        "discovery_result_path": "DEYE_BRIDGE_DISCOVERY_RESULT_PATH",
        "discovery_wait_timeout": "DEYE_BRIDGE_DISCOVERY_WAIT_TIMEOUT",
        "poll_interval": "DEYE_BRIDGE_POLL_INTERVAL",
        "request_timeout": "DEYE_BRIDGE_REQUEST_TIMEOUT",
        "scan_timeout": "DEYE_BRIDGE_SCAN_TIMEOUT",
        "log_level": "DEYE_BRIDGE_LOG_LEVEL",
    }
    for key, env_name in env_map.items():
        os.environ[env_name] = str(settings[key])


class BridgeServiceRunner:
    def __init__(self, include_console: bool) -> None:
        settings = load_service_settings()
        configure_logging(settings, include_console)
        apply_environment(settings)

        from app import app

        self.server = ServiceSafeUvicornServer(
            uvicorn.Config(
                app,
                host=str(settings["bind_host"]),
                port=int(settings["port"]),
                log_level=str(settings["log_level"]),
                log_config=None,
                access_log=True,
            )
        )
        self.server.install_signal_handlers = lambda: None
        self.loop: asyncio.AbstractEventLoop | None = None

    def run(self) -> None:
        if sys.platform == "win32" and hasattr(asyncio, "WindowsSelectorEventLoopPolicy"):
            asyncio.set_event_loop_policy(asyncio.WindowsSelectorEventLoopPolicy())

        self.loop = asyncio.new_event_loop()
        asyncio.set_event_loop(self.loop)
        try:
            self.loop.run_until_complete(self.server.serve())
        finally:
            try:
                pending = asyncio.all_tasks(self.loop)
                for task in pending:
                    task.cancel()
                if pending:
                    self.loop.run_until_complete(asyncio.gather(*pending, return_exceptions=True))
                self.loop.run_until_complete(self.loop.shutdown_asyncgens())
            finally:
                self.loop.close()
                asyncio.set_event_loop(None)
                self.loop = None

    def stop(self) -> None:
        self.server.should_exit = True
        if self.loop is not None:
            self.loop.call_soon_threadsafe(lambda: None)


class BridgeWindowsService(win32serviceutil.ServiceFramework):
    _svc_name_ = "DeyeHomeBridge"
    _svc_display_name_ = "Deye Home Bridge"
    _svc_description_ = "Runs the Deye home bridge API and admin UI directly on the Windows host."

    def __init__(self, args: list[str]) -> None:
        super().__init__(args)
        self.runner: BridgeServiceRunner | None = None

    def SvcStop(self) -> None:
        self.ReportServiceStatus(win32service.SERVICE_STOP_PENDING)
        if self.runner is not None:
            self.runner.stop()

    def SvcDoRun(self) -> None:
        servicemanager.LogInfoMsg("Deye Home Bridge service starting")
        self.runner = BridgeServiceRunner(include_console=False)
        self.ReportServiceStatus(win32service.SERVICE_RUNNING)
        try:
            self.runner.run()
        finally:
            servicemanager.LogInfoMsg("Deye Home Bridge service stopped")


def run_console() -> None:
    runner = BridgeServiceRunner(include_console=True)
    try:
        runner.run()
    except KeyboardInterrupt:
        runner.stop()


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1].lower() == "run":
        run_console()
    else:
        win32serviceutil.HandleCommandLine(BridgeWindowsService)
