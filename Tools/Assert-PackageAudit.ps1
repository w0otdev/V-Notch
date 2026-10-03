[CmdletBinding()]
param([string]$Solution = 'V-Notch.sln', [string]$OutputPath = 'artifacts/package-audit.json')

$ErrorActionPreference = 'Stop'
$auditOutput = & dotnet list $Solution package --vulnerable --include-transitive --format json --output-version 1
if ($LASTEXITCODE -ne 0) { throw 'Package vulnerability lookup failed.' }
$auditText = $auditOutput -join [Environment]::NewLine
$audit = $auditText | ConvertFrom-Json
if ($audit.version -ne 1 -or @($audit.projects).Count -eq 0) { throw 'Invalid or empty package audit result.' }
$auditDirectory = Split-Path $OutputPath -Parent
if ($auditDirectory) { New-Item -ItemType Directory -Path $auditDirectory -Force | Out-Null }
Set-Content -LiteralPath $OutputPath -Value $auditText -Encoding utf8
$findings = @($audit.projects | ForEach-Object {
    foreach ($framework in $_.frameworks) {
        foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
            foreach ($vulnerability in $package.vulnerabilities) {
                '{0} {1}: {2} ({3})' -f $package.id, $package.resolvedVersion, $vulnerability.severity, $vulnerability.advisoryurl
            }
        }
    }
})
if ($findings.Count -gt 0) { throw ('Vulnerable packages detected:' + [Environment]::NewLine + ($findings -join [Environment]::NewLine)) }
Write-Host 'No known vulnerabilities reported for direct or transitive packages.'
