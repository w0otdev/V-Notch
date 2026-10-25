# Exercise the runner with fake commands; no build, network, or application processes.
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('vnotch-ci-resume-' + [guid]::NewGuid().ToString('N'))
try {
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
    $global:CiResumeFailBuild = $false
    function global:python {
        $global:CiResumeCalls.Add('python')
        $global:LASTEXITCODE = 0
    }
    function global:dotnet {
        $global:CiResumeCalls.Add([string]$args[0])
        $global:LASTEXITCODE = if (($args[0] -eq 'format' -and $global:CiResumeFailFormat) -or
            ($args[0] -eq 'build' -and $global:CiResumeFailBuild)) { 1 } else { 0 }
    }
    $runner = Join-Path $fixture 'scripts/c.ps1'
    $failed = $false
    try { & $runner *> $null } catch { $failed = $_.Exception.Message -eq 'Code formatting verification failed.' }
    if (-not $failed) { throw 'The fixture must fail at formatting.' }
    $run = @(Get-ChildItem -LiteralPath (Join-Path $fixture 'artifacts') -Directory)[0].FullName
    $state = Get-Content -LiteralPath (Join-Path $run 'checkpoint.json') -Raw | ConvertFrom-Json
    if ($state.NextStep -ne 4) { throw 'Failure must preserve the first unfinished step.' }
    $callsBefore = $global:CiResumeCalls.Count
    $rejected = $false
    try { & $runner -Resume $run -Fast *> $null } catch { $rejected = $_.Exception.Message -like 'Resume requires*' }
    if (-not $rejected -or $global:CiResumeCalls.Count -ne $callsBefore) { throw 'Changed validation options must be rejected before running checks.' }
    $global:CiResumeFailFormat = $false
    $global:CiResumeFailBuild = $true
    $failed = $false
    try { & $runner -Resume $run *> $null } catch { $failed = $_.Exception.Message -eq 'Build failed with warnings or errors.' }
    if (-not $failed) { throw 'The second attempt must fail at build.' }
    $state = Get-Content -LiteralPath (Join-Path $run 'checkpoint.json') -Raw | ConvertFrom-Json
    if ($state.NextStep -ne 5) { throw 'Build failure must preserve step 5.' }
    $failedResults = $state.TestResultsDirectory
    Set-Content -LiteralPath (Join-Path $failedResults 'failure-evidence.txt') -Value 'Keep this evidence.'
    $global:CiResumeFailBuild = $false
    & $runner -Resume $run *> $null
    $state = Get-Content -LiteralPath (Join-Path $run 'checkpoint.json') -Raw | ConvertFrom-Json
    if ($state.NextStep -ne 9) { throw 'Successful resume must finish every remaining stage.' }
    if ($state.TestResultsDirectory -eq $failedResults -or -not (Test-Path (Join-Path $failedResults 'failure-evidence.txt'))) {
        throw 'Retry must isolate new evidence directory and preserve failed evidence.'
    }
    if (($global:CiResumeCalls -join ',') -ne 'python,python,restore,format,format,build,build') {
        throw "Resume repeated or omitted checks: $($global:CiResumeCalls -join ',')"
    }
    $callsBefore = $global:CiResumeCalls.Count
    & $runner -Resume $run *> $null
    if ($global:CiResumeCalls.Count -ne $callsBefore) { throw 'Completed runs must not repeat checks.' }
    Write-Host 'Local CI resume regression checks passed.'
} finally {
    Remove-Item -LiteralPath 'Function:\python' -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath 'Function:\dotnet' -Force -ErrorAction SilentlyContinue
    Remove-Variable -Name CiResumeCalls, CiResumeFailFormat, CiResumeFailBuild -Scope Global -ErrorAction SilentlyContinue
    $global:LASTEXITCODE = 0
    $resolved = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolved) -like 'vnotch-ci-resume-*' -and
        (Test-Path -LiteralPath $resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
