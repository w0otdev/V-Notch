[CmdletBinding()]
param([string]$Solution = 'V-Notch.sln', [string]$OutputPath = 'artifacts/package-audit.json')

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ge 7) {
    $PSNativeCommandUseErrorActionPreference = $false
}
$prevErr = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    $auditOutput = & dotnet list $Solution package --vulnerable --include-transitive --format json --output-version 1
} finally {
    $ErrorActionPreference = $prevErr
}
if ($LASTEXITCODE -ne 0) { throw 'Package vulnerability lookup failed.' }
$auditText = $auditOutput -join [Environment]::NewLine
$audit = $auditText | ConvertFrom-Json
if ($audit.version -ne 1 -or @($audit.projects).Count -eq 0) { throw 'Invalid or empty package audit result.' }
$auditDirectory = Split-Path $OutputPath -Parent
if ($auditDirectory) { New-Item -ItemType Directory -Path $auditDirectory -Force | Out-Null }
Set-Content -LiteralPath $OutputPath -Value $auditText -Encoding utf8
$findings = @($audit.projects | Where-Object { $_ -and $_.frameworks } | ForEach-Object {
    foreach ($framework in @($_.frameworks)) {
        if (-not $framework) { continue }
        foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
            if (-not $package -or -not $package.vulnerabilities) { continue }
            foreach ($vulnerability in @($package.vulnerabilities)) {
                if (-not $vulnerability) { continue }
                '{0} {1}: {2} ({3})' -f $package.id, $package.resolvedVersion, $vulnerability.severity, $vulnerability.advisoryurl
            }
        }
    }
})
if ($findings.Count -gt 0) { throw ('Vulnerable packages detected:' + [Environment]::NewLine + ($findings -join [Environment]::NewLine)) }
Write-Host 'No known vulnerabilities reported for direct or transitive packages.'
