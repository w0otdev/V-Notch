$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('vnotch-desktop-gates-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
$gate = Join-Path $repository 'Tools/Assert-DesktopTests.ps1'
$previousSummary = $env:GITHUB_STEP_SUMMARY
$env:GITHUB_STEP_SUMMARY = $null

function Assert-Rejected([scriptblock]$Action, [string]$Message) {
    try { & $Action } catch {
        if ($_.Exception.Message -notmatch $Message) { throw }
        return
    }
    throw "Desktop gate accepted invalid input; expected: $Message"
}

function Write-Report([int]$Total, [int]$Executed, [int]$Passed, [int]$Failed,
    [int]$SkippedResults = 0, [int]$SkippedCounter = 0, [string]$Outcome = 'Completed') {
    $results = (@(1..$SkippedResults | Where-Object { $SkippedResults -gt 0 } | ForEach-Object {
        '<UnitTestResult testName="Skipped test" outcome="NotExecuted" />'
    }) -join '')
    @"
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>$results</Results>
  <ResultSummary outcome="$Outcome">
    <Counters total="$Total" executed="$Executed" passed="$Passed" failed="$Failed" notExecuted="$SkippedCounter" />
  </ResultSummary>
</TestRun>
"@ | Set-Content -LiteralPath (Join-Path $temporary 'desktop.trx')
}

try {
    Assert-Rejected { & $gate -ResultsDirectory $temporary } 'Exactly one desktop TRX'
    Write-Report 10 10 10 0
    & $gate -ResultsDirectory $temporary
    # The user's actual failure: xUnit skips seven facts but VSTest reports zero
    # in the summary counter. Report the skips without accepting the failed run.
    Write-Report 10 3 2 1 -SkippedResults 7 -Outcome 'Failed'
    Assert-Rejected { & $gate -ResultsDirectory $temporary } 'total 10, executed 3, passed 2, failed 1, skipped 7'
    Write-Report 10 10 10 0 -SkippedResults 1
    Assert-Rejected { & $gate -ResultsDirectory $temporary } 'skipped 1'
    Write-Report 10 10 10 0 -SkippedCounter 1
    Assert-Rejected { & $gate -ResultsDirectory $temporary } 'skipped 1'
    Write-Report 10 9 9 0
    Assert-Rejected { & $gate -ResultsDirectory $temporary } 'executed 9'
    Write-Report 10 10 9 1 -Outcome 'Failed'
    Assert-Rejected { & $gate -ResultsDirectory $temporary } 'failed 1'
    Write-Report 0 0 0 0
    Assert-Rejected { & $gate -ResultsDirectory $temporary } 'total 0'
    Write-Report 10 10 10 0 -Outcome 'Aborted'
    Assert-Rejected { & $gate -ResultsDirectory $temporary } 'Desktop gate failed'
    Copy-Item -LiteralPath (Join-Path $temporary 'desktop.trx') -Destination (Join-Path $temporary 'duplicate.trx')
    Assert-Rejected { & $gate -ResultsDirectory $temporary } 'Exactly one desktop TRX'

    # Exercise setup's refusal path without downloading/installing anything or
    # changing local services. Even a self-hosted Actions environment must fail.
    $previous = @{}
    foreach ($name in 'GITHUB_ACTIONS', 'RUNNER_ENVIRONMENT', 'RUNNER_OS') {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name)
    }
    try {
        $env:GITHUB_ACTIONS = 'true'
        $env:RUNNER_OS = 'Windows'
        foreach ($runner in @('', 'self-hosted')) {
            $env:RUNNER_ENVIRONMENT = $runner
            Assert-Rejected { & (Join-Path $repository 'Tools/Initialize-DesktopTestRunner.ps1') } 'restricted to disposable GitHub-hosted'
        }
    } finally {
        foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
    }
    Write-Host 'Desktop gate regressions passed.'
} finally {
    $env:GITHUB_STEP_SUMMARY = $previousSummary
    $resolved = [IO.Path]::GetFullPath($temporary)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notlike 'vnotch-desktop-gates-*') { throw 'Unsafe temporary cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
