[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ResultsDirectory,
    [ValidateRange(0, 100)][decimal]$MinimumPercent = 80
)

$ErrorActionPreference = 'Stop'
$reports = @(Get-ChildItem -LiteralPath $ResultsDirectory -Recurse -File -Filter '*.cobertura.xml')
if ($reports.Count -eq 0) { throw 'No Cobertura coverage report was produced.' }
# Microsoft.CodeCoverage can write the same attachment in both the run folder and
# the collector folder. Accept identical copies, but never pick an arbitrary run.
$hashes = @($reports | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } | Select-Object -Unique)
if ($hashes.Count -ne 1) { throw 'Found different coverage reports; use a clean results directory for one test run.' }

$xmlSettings = [System.Xml.XmlReaderSettings]::new()
$xmlSettings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
$xmlSettings.XmlResolver = $null
$reader = [System.Xml.XmlReader]::Create($reports[0].FullName, $xmlSettings)
try {
    $document = [System.Xml.XmlDocument]::new()
    $document.XmlResolver = $null
    $document.Load($reader)
} finally { $reader.Dispose() }

$packages = @($document.SelectNodes('/coverage/packages/package'))
if ($packages.Count -ne 1 -or $packages[0].GetAttribute('name') -ne 'V-Notch') {
    throw 'Coverage must contain the entire V-Notch application assembly only.'
}

$culture = [System.Globalization.CultureInfo]::InvariantCulture
$valid = [long]::Parse($document.DocumentElement.GetAttribute('lines-valid'), $culture)
$covered = [long]::Parse($document.DocumentElement.GetAttribute('lines-covered'), $culture)
if ($valid -le 0 -or $covered -lt 0 -or $covered -gt $valid) {
    throw 'Coverage report is empty or has invalid line counts.'
}
$percent = [decimal]$covered * 100 / $valid
$message = 'Application line coverage: {0:F2}% ({1}/{2}); required: {3:F2}%' -f $percent, $covered, $valid, $MinimumPercent
Write-Host $message
if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $message }
if ($percent -lt $MinimumPercent) { throw 'Application coverage is below the required threshold.' }
