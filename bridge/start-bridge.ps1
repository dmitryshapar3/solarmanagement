$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$existing = Get-CimInstance Win32_Process | Where-Object {
  $_.Name -match '^python(.exe)?$' -and $_.CommandLine -like '*windows_service.py*run*'
}
if ($existing) {
  Write-Output "Bridge host process is already running."
  exit 0
}

Start-Process -FilePath python.exe -ArgumentList @((Join-Path $root "windows_service.py"), "run") -WorkingDirectory $root -WindowStyle Hidden
Write-Output "Bridge host process started."
