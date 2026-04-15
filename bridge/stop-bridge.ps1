$processes = Get-CimInstance Win32_Process | Where-Object {
  $_.Name -match '^python(.exe)?$' -and $_.CommandLine -like '*windows_service.py*run*'
}
if (-not $processes) {
  Write-Output "Bridge host process is not running."
  exit 0
}

$processes | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Write-Output "Bridge host process stopped."
