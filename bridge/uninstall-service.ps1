$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root
try {
  Unregister-ScheduledTask -TaskName "DeyeHomeBridgeDiscovery" -Confirm:$false -ErrorAction SilentlyContinue
  python .\windows_service.py stop | Out-Null
  python .\windows_service.py remove | Out-Null
} finally {
  Pop-Location
}
