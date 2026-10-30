[CmdletBinding()]
param([string]$AssetsDirectory, [string]$ReleaseVersion)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$probe = Join-Path $PSScriptRoot 'Assert-UpdateCompatibility.ps1'
$baseline = @{}
$null = & git -C $repository rev-parse --verify --quiet 'refs/tags/v2.2.0^{commit}' 2>$null
if ($LASTEXITCODE -eq 0) {
    $baseline.ClientRef = 'v2.2.0'
    Write-Host 'Using the frozen 2.2.0 updater and its native DLL.'
} else {
    [xml]$project = Get-Content -LiteralPath (Join-Path $repository 'V-Notch.csproj')
    $current = [string]($project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
    if ($current -ne '2.2.0') { throw 'Fetch the official v2.2.0 tag before verifying future update compatibility.' }
    Write-Host 'Preparing 2.2.0 baseline from this checkout; the official tag is not published yet.'
}

# Discovery, signed manifests, both installer variants, channel selection and
# rejection of tampered installers/signatures. Synthetic payloads are never run.
foreach ($target in '2.2.1', '2.3.0', '3.0.0') {
    & $probe -InstalledVersion '2.2.0' -ReleaseVersion $target @baseline
}

if ($AssetsDirectory) {
    if (-not $ReleaseVersion) { throw '-ReleaseVersion is required with -AssetsDirectory.' }
    if ([version]($ReleaseVersion.Split('-')[0]) -gt [version]'2.2.0') {
        & $probe -InstalledVersion '2.2.0' -ReleaseVersion $ReleaseVersion -AssetsDirectory $AssetsDirectory @baseline
    }
}
