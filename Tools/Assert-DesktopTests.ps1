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
# VSTest's xUnit adapter can report notExecuted=0 for skipped facts. The result
# records still carry NotExecuted, so do not hide them in the failure diagnostic.
$skipped = [Math]::Max([int]$counters.notExecuted,
    @($doc.TestRun.Results.UnitTestResult | Where-Object outcome -eq 'NotExecuted').Count)
if ($total -le 0 -or $executed -ne $total -or $passed -ne $total -or $failed -ne 0 -or $skipped -ne 0 -or
    $doc.TestRun.ResultSummary.outcome -ne 'Completed') {
    throw "Desktop gate failed: total $total, executed $executed, passed $passed, failed $failed, skipped $skipped. See the TRX failures and skip reasons; visible tests require an opted-in GitHub-hosted Windows desktop and browser audio requires an active render endpoint."
}
$summary = "Desktop integration: $passed/$total passed; no skipped tests."
Write-Host $summary
if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $summary }
