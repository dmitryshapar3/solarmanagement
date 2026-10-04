param(
    [Parameter(Mandatory = $true)][string]$SigningKeyPath,
    [Parameter(Mandatory = $true)][string]$PublicKeyPath,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$InstalledBundleDirectory = '/app/integration-bundle',
    [string]$InstalledPackageDirectory = '/data/integration-packages',
    [string]$InstalledKeyRingDirectory = '/data/keys/integrations',
    [switch]$IncludeWeb
)
$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path -Parent $PSScriptRoot
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'The deployment output must be a new directory.' }
if ([System.IO.Path]::GetFullPath($SigningKeyPath).StartsWith($output + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The private signing key must remain outside the deployment bundle.'
}
New-Item -ItemType Directory -Path $output | Out-Null
& dotnet run --project (Join-Path $projectDirectory 'tools/SolarManagement.IntegrationPackager') --configuration Release -- --check-key-pair $SigningKeyPath $PublicKeyPath
if ($LASTEXITCODE -ne 0) { throw 'Publisher key validation failed.' }
$bundle = Join-Path $output 'integration-bundle'
New-Item -ItemType Directory -Path $bundle | Out-Null
Copy-Item -LiteralPath $PublicKeyPath -Destination (Join-Path $bundle 'publisher-public-key.pem')
$entries = @()
$origins = @()
$providers = @(Get-ChildItem -LiteralPath (Join-Path $projectDirectory 'integrations') -Directory -Filter 'SolarManagement.Providers.*' | Sort-Object Name)
foreach ($project in $providers) {
    $manifestPath = Join-Path $project.FullName 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        $legacyId = switch ($project.Name) {
            'SolarManagement.Providers.DeyeCloud' { 'deye.cloud' }
            'SolarManagement.Providers.ShellyCloud' { 'shelly.cloud' }
            default { throw ('Provider manifest is missing: ' + $project.Name) }
        }
        $manifestPath = Join-Path $projectDirectory ('integrations/manifests/' + $legacyId + '.json')
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $provider = @{ Id = $manifest.providerId; Project = $project.Name }
    $origins += @($manifest.allowedOrigins)
    $published = Join-Path $output ('worker-' + $provider.Id)
    & dotnet publish (Join-Path $projectDirectory ('integrations/' + $provider.Project)) -c Release -o $published
    if ($LASTEXITCODE -ne 0) { throw 'Provider publication failed.' }
    $archiveName = $provider.Id + '-' + $manifest.packageVersion + '.zip'
    & dotnet run --project (Join-Path $projectDirectory 'tools/SolarManagement.IntegrationPackager') --configuration Release -- $published $manifestPath $SigningKeyPath (Join-Path $bundle $archiveName)
    if ($LASTEXITCODE -ne 0) { throw 'Provider signing failed.' }
    $digest = (Get-FileHash -LiteralPath (Join-Path $bundle $archiveName) -Algorithm SHA256).Hash
    $entries += @{ ArchivePath = $InstalledBundleDirectory.TrimEnd('/', '\') + '/' + $archiveName; ExpectedSha256 = $digest }
}
$configuration = @{ Integrations = @{ KeyRingPath = $InstalledKeyRingDirectory }; IntegrationRuntime = @{
    PackageDirectory = $InstalledPackageDirectory
    TrustedPublisherPublicKeyFiles = @{ 'solar-management' = $InstalledBundleDirectory.TrimEnd('/', '\') + '/publisher-public-key.pem' }
    ApprovedOrigins = @($origins | Sort-Object -Unique)
    BootstrapPackages = $entries
} }
$configuration | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'integration-bootstrap.json') -Encoding utf8
if ($IncludeWeb) {
    & dotnet publish (Join-Path $projectDirectory 'src/DeyeSolar.Web') -c Release -o (Join-Path $output 'web')
    if ($LASTEXITCODE -ne 0) { throw 'Web publication failed.' }
}
Write-Output ('Deployment bundle: ' + $output)
