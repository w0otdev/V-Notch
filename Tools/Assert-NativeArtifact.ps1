[CmdletBinding()]
param(
    [string]$ArtifactDirectory = 'artifacts/native',
    [string]$ExpectedCommit = '',
    [string]$BinaryPath = ''
)
$ErrorActionPreference = 'Stop'
if (-not $ExpectedCommit) {
    $ExpectedCommit = (& git -C (Split-Path $PSScriptRoot -Parent) rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify expected native source commit.' }
}
if ($ExpectedCommit -cnotmatch '^[0-9a-f]{40}$') { throw 'A full source commit is required.' }
$manifest = Get-Content -LiteralPath (Join-Path $ArtifactDirectory 'provenance.json') -Raw | ConvertFrom-Json
if ($manifest.commit -cne $ExpectedCommit -or $manifest.configuration -cne 'Release' -or $manifest.sha256 -notmatch '^[0-9a-fA-F]{64}$') {
    throw 'Native artifact provenance does not match the tested release commit.'
}
if (-not $BinaryPath) { $BinaryPath = Join-Path $ArtifactDirectory 'VNotch.Native.dll' }
if ((Get-FileHash -LiteralPath $BinaryPath -Algorithm SHA256).Hash -ne $manifest.sha256) {
    throw 'Native artifact hash mismatch.'
}
Write-Host "Native artifact verified for $ExpectedCommit."
