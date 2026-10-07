# Exercise the runner with fake commands; no build, network, or application processes.
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('vnotch-ci-resume-' + [guid]::NewGuid().ToString('N'))
foreach ($directory in @('scripts', 'Tools', 'Tests')) {
    New-Item -ItemType Directory -Path (Join-Path $fixture $directory) -Force | Out-Null
}
Copy-Item -LiteralPath (Join-Path $repository 'scripts/c.ps1') -Destination (Join-Path $fixture 'scripts/c.ps1')
foreach ($script in @('Tools/Assert-ModelAssets.ps1', 'Tools/Assert-UpdateCompatibility.ps1',
        'Tools/Assert-PackageAudit.ps1', 'Tools/Assert-Coverage.ps1', 'Tests/ReleaseGateTests.ps1',
        'Tests/PrepushCleanupTests.ps1', 'Tests/LocalCiResumeTests.ps1')) {
    Set-Content -LiteralPath (Join-Path $fixture $script) -Value '# Fixture: success.'
}
$global:CiResumeCalls = [Collections.Generic.List[string]]::new()
$global:CiResumeFailFormat = $true
$global:CiResumeFailTest = $false
function global:python {
    $global:CiResumeCalls.Add('python')
    $global:LASTEXITCODE = 0
}
function global:dotnet {
    $global:CiResumeCalls.Add([string]$args[0])
    $global:LASTEXITCODE = if (($args[0] -eq 'format' -and $global:CiResumeFailFormat) -or
        ($args[0] -eq 'test' -and $global:CiResumeFailTest)) { 1 } else { 0 }
}
$runner = Join-Path $fixture 'scripts/c.ps1'
$failed = $false
try { & $runner } catch { $failed = $_.Exception.Message -eq 'Code formatting verification failed.' }
if (-not $failed) { throw 'The fixture must fail at formatting.' }
$run = @(Get-ChildItem -LiteralPath (Join-Path $fixture 'artifacts') -Directory)[0].FullName
$state = Get-Content -LiteralPath (Join-Path $run 'checkpoint.json') -Raw | ConvertFrom-Json
if ($state.NextStep -ne 4) { throw 'Failure must preserve the first unfinished step.' }
$callsBefore = $global:CiResumeCalls.Count
$rejected = $false
try { & $runner -Resume $run -Fast } catch { $rejected = $_.Exception.Message -like 'Resume requires*' }
if (-not $rejected -or $global:CiResumeCalls.Count -ne $callsBefore) { throw 'Changed validation options must be rejected before running checks.' }
$global:CiResumeFailFormat = $false
$global:CiResumeFailTest = $true
$failed = $false
try { & $runner -Resume $run } catch { $failed = $_.Exception.Message -eq 'Test run failed.' }
if (-not $failed) { throw 'The second attempt must fail at tests.' }
$state = Get-Content -LiteralPath (Join-Path $run 'checkpoint.json') -Raw | ConvertFrom-Json
if ($state.NextStep -ne 6) { throw 'Test failure must preserve step 6.' }
$failedResults = $state.TestResultsDirectory
Set-Content -LiteralPath (Join-Path $failedResults 'failure-evidence.txt') -Value 'Keep this evidence.'
$global:CiResumeFailTest = $false
& $runner -Resume $run
$state = Get-Content -LiteralPath (Join-Path $run 'checkpoint.json') -Raw | ConvertFrom-Json
if ($state.NextStep -ne 11) { throw 'Successful resume must finish every remaining stage.' }
if ($state.TestResultsDirectory -eq $failedResults -or -not (Test-Path (Join-Path $failedResults 'failure-evidence.txt'))) {
    throw 'Test retry must isolate new coverage and preserve failed evidence.'
}
if (($global:CiResumeCalls -join ',') -ne 'python,python,restore,format,format,build,test,test') {
    throw "Resume repeated or omitted checks: $($global:CiResumeCalls -join ',')"
}
$callsBefore = $global:CiResumeCalls.Count
& $runner -Resume $run
if ($global:CiResumeCalls.Count -ne $callsBefore) { throw 'Completed runs must not repeat checks.' }
Write-Host 'Local CI resume regression checks passed.'
