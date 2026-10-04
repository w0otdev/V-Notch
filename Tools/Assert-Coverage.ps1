[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ResultsDirectory,
    [ValidateRange(0, 100)][decimal]$MinimumPercent = 70,
    [ValidateNotNullOrEmpty()][string]$AssemblyName = 'V-Notch'
)

$ErrorActionPreference = 'Stop'
$reports = @(Get-ChildItem -LiteralPath $ResultsDirectory -Recurse -File -Filter '*.cobertura.xml')
if ($reports.Count -eq 0) { throw 'No Cobertura coverage report was produced.' }
# Microsoft.CodeCoverage can write the same attachment in both the run folder and
# the collector folder. Deduplicate those copies, then merge distinct suite reports.
$seenHashes = [System.Collections.Generic.HashSet[string]]::new()
$reports = @($reports | Where-Object { $seenHashes.Add((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash) })

$xmlSettings = [System.Xml.XmlReaderSettings]::new()
$xmlSettings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
$xmlSettings.XmlResolver = $null
$culture = [System.Globalization.CultureInfo]::InvariantCulture
$metadata = @(foreach ($report in $reports) {
    $reader = [System.Xml.XmlReader]::Create($report.FullName, $xmlSettings)
    try {
        if ($reader.MoveToContent() -ne [System.Xml.XmlNodeType]::Element -or $reader.LocalName -ne 'coverage') {
            throw 'Expected a Cobertura coverage root.'
        }
        $valid = [long]::Parse($reader.GetAttribute('lines-valid'), $culture)
        $covered = [long]::Parse($reader.GetAttribute('lines-covered'), $culture)
        if ($valid -lt 0 -or $covered -lt 0 -or $covered -gt $valid) {
            throw 'Coverage report is empty or has invalid line counts.'
        }
        $packages = @(while ($reader.ReadToFollowing('package')) { $reader.GetAttribute('name') })
        if ($valid -eq 0 -and $packages -ccontains $AssemblyName) {
            throw 'Coverage report is empty or has invalid line counts.'
        }
        [pscustomobject]@{ Path = $report.FullName; Valid = $valid; Covered = $covered; Packages = $packages }
    } finally { $reader.Dispose() }
})
$targetReports = @($metadata | Where-Object { $_.Packages -ccontains $AssemblyName })
if ($targetReports.Count -eq 0) { throw "No coverage report contains the $AssemblyName application assembly." }

if ($targetReports.Count -eq 1 -and $targetReports[0].Packages.Count -eq 1) {
    # The usual single-assembly run needs only root counters, never an XML DOM.
    $valid = $targetReports[0].Valid
    $covered = $targetReports[0].Covered
} else {
    # Preserve the collector's counting convention. Microsoft counts method lines;
    # other collectors count class lines. Never mix both copies of the same data.
    $classes = [System.Collections.Generic.Dictionary[string, System.Collections.Generic.Dictionary[int, bool]]]::new([StringComparer]::Ordinal)
    $methods = [System.Collections.Generic.Dictionary[string, System.Collections.Generic.Dictionary[int, bool]]]::new([StringComparer]::Ordinal)
    $classCountersMatch = $true
    $methodCountersMatch = $true
    foreach ($report in $targetReports) {
        $reader = [System.Xml.XmlReader]::Create($report.Path, $xmlSettings)
        try {
            $classDepth = -1
            $targetPackage = $false
            $classLines = $null
            $methodLines = $null
            [long]$classValid = 0; [long]$classCovered = 0
            [long]$methodValid = 0; [long]$methodCovered = 0
            while ($reader.Read()) {
                if ($reader.NodeType -ne [System.Xml.XmlNodeType]::Element) { continue }
                if ($reader.LocalName -eq 'package') {
                    $targetPackage = $reader.GetAttribute('name') -ceq $AssemblyName
                } elseif ($reader.LocalName -eq 'class') {
                    $classDepth = $reader.Depth
                    if ($targetPackage) {
                        $filename = $reader.GetAttribute('filename')
                        $name = $reader.GetAttribute('name')
                        if ([string]::IsNullOrWhiteSpace($filename) -or [string]::IsNullOrWhiteSpace($name)) {
                            throw 'Coverage class is missing its source file or identity.'
                        }
                        $key = $filename.Replace('\', '/') + "`0" + $name
                        if (-not $classes.TryGetValue($key, [ref]$classLines)) {
                            $classLines = [System.Collections.Generic.Dictionary[int, bool]]::new()
                            $classes.Add($key, $classLines)
                        }
                    }
                } elseif ($reader.LocalName -eq 'method' -and $targetPackage) {
                    $methodKey = $key + "`0" + $reader.GetAttribute('name') + "`0" + $reader.GetAttribute('signature')
                    if (-not $methods.TryGetValue($methodKey, [ref]$methodLines)) {
                        $methodLines = [System.Collections.Generic.Dictionary[int, bool]]::new()
                        $methods.Add($methodKey, $methodLines)
                    }
                } elseif ($reader.LocalName -eq 'line' -and $classDepth -ge 0) {
                    $number = [int]::Parse($reader.GetAttribute('number'), $culture)
                    $hits = [long]::Parse($reader.GetAttribute('hits'), $culture)
                    if ($number -le 0 -or $hits -lt 0) { throw 'Coverage line has invalid number or hit count.' }
                    if ($reader.Depth -eq $classDepth + 2) {
                        $classValid++; if ($hits -gt 0) { $classCovered++ }
                        $lines = $classLines
                    } elseif ($reader.Depth -eq $classDepth + 4) {
                        $methodValid++; if ($hits -gt 0) { $methodCovered++ }
                        $lines = $methodLines
                    } else { continue }
                    if ($targetPackage) {
                        $wasCovered = $false
                        [void]$lines.TryGetValue($number, [ref]$wasCovered)
                        $lines[$number] = $wasCovered -or $hits -gt 0
                    }
                }
            }
            $classCountersMatch = $classCountersMatch -and $classValid -eq $report.Valid -and $classCovered -eq $report.Covered
            $methodCountersMatch = $methodCountersMatch -and $methodValid -eq $report.Valid -and $methodCovered -eq $report.Covered
        } finally { $reader.Dispose() }
    }
    if ($classCountersMatch) { $merged = $classes }
    elseif ($methodCountersMatch) { $merged = $methods }
    else { throw 'Distinct or mixed-assembly coverage reports require consistent application line details to merge.' }
    [long]$valid = 0
    [long]$covered = 0
    foreach ($lines in $merged.Values) {
        $valid += $lines.Count
        foreach ($hit in $lines.Values) { if ($hit) { $covered++ } }
    }
    if ($valid -le 0) { throw 'Distinct or mixed-assembly coverage reports require application line details to merge.' }
}
$percent = [decimal]$covered * 100 / $valid
$message = 'Application line coverage: {0:F2}% ({1}/{2}); required: {3:F2}%' -f $percent, $covered, $valid, $MinimumPercent
Write-Host $message
if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $message }
if ($percent -lt $MinimumPercent) { throw 'Application coverage is below the required threshold.' }
