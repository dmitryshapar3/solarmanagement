param(
  [string]$DiscoveryTaskUser
)

$ErrorActionPreference = "Stop"

if (-not $DiscoveryTaskUser) {
  $DiscoveryTaskUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
}

function Test-IsAdministrator {
  $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
  $principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
  return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-PythonChecked {
  param(
    [Parameter(Mandatory = $true)]
    [string[]]$Arguments
  )

  & python @Arguments
  if ($LASTEXITCODE -ne 0) {
    throw "python $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
  }
}

function Register-DiscoveryTask {
  param(
    [Parameter(Mandatory = $true)]
    [string]$TaskUser
  )

  $pythonExe = (Get-Command python.exe -ErrorAction Stop).Source
  $workerScript = Join-Path $root "discovery_worker.py"
  $action = New-ScheduledTaskAction -Execute $pythonExe -Argument "`"$workerScript`"" -WorkingDirectory $root
  $principal = New-ScheduledTaskPrincipal -UserId $TaskUser -LogonType Interactive -RunLevel Highest
  $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew

  Register-ScheduledTask `
    -TaskName "DeyeHomeBridgeDiscovery" `
    -Description "Runs one-shot TinyTuya discovery for Deye Home Bridge in the interactive user session." `
    -Action $action `
    -Principal $principal `
    -Settings $settings `
    -Force | Out-Null
}

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root
try {
  if (-not (Test-IsAdministrator)) {
    throw "Run bridge\\install-service.ps1 from an elevated PowerShell window."
  }

  Write-Host "Installing DeyeHomeBridge service as LocalSystem"
  Write-Host "Registering DeyeHomeBridgeDiscovery task for interactive user $DiscoveryTaskUser"
  Invoke-PythonChecked @("-m", "pip", "install", "-r", "requirements.txt")

  if (-not (Test-Path (Join-Path $root "service-config.json"))) {
    Copy-Item (Join-Path $root "service-config.example.json") (Join-Path $root "service-config.json")
    throw "Created bridge\\service-config.json from the example. Fill in bridge_token if needed, then run this script again."
  }

  Register-DiscoveryTask -TaskUser $DiscoveryTaskUser

  python .\windows_service.py stop | Out-Null
  python .\windows_service.py remove | Out-Null
  Invoke-PythonChecked @(
    ".\windows_service.py",
    "--startup", "auto",
    "install"
  )
  Invoke-PythonChecked @(".\windows_service.py", "start")
} finally {
  Pop-Location
}
