[CmdletBinding()]
param([Parameter(Mandatory)][string]$ResultsDirectory)
$ErrorActionPreference = 'Stop'
$reports = @(Get-ChildItem -LiteralPath $ResultsDirectory -Recurse -File -Filter '*.trx')
if ($reports.Count -ne 1) { throw 'Exactly one desktop TRX report is required.' }
$settings = [Xml.XmlReaderSettings]::new()
$settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
$settings.XmlResolver = $null
$reader = [Xml.XmlReader]::Create($reports[0].FullName, $settings)
try { $doc = [Xml.XmlDocument]::new(); $doc.Load($reader) } finally { $reader.Dispose() }
$counters = $doc.TestRun.ResultSummary.Counters
$total = [int]$counters.total
$executed = [int]$counters.executed
$passed = [int]$counters.passed
$failed = [int]$counters.failed
$skipped = [int]$counters.notExecuted
if ($total -le 0 -or $executed -ne $total -or $passed -ne $total -or $failed -ne 0 -or $skipped -ne 0 -or
    $doc.TestRun.ResultSummary.outcome -ne 'Completed') {
    throw "Desktop gate failed: total $total, executed $executed, passed $passed, failed $failed, skipped $skipped. A disposable non-interactive Windows runner is required."
}
$summary = "Desktop integration: $passed/$total passed; no skipped tests."
Write-Host $summary
if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $summary }
