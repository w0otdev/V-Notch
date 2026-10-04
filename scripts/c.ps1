<#
.SYNOPSIS
    Full CI Verification Script for V-Notch.
    Runs the Windows validation and pipeline checks locally before pushing.
    Hosted Gitleaks history scanning and CodeQL analysis still run in GitHub Actions.

.DESCRIPTION
    Runs the complete Windows CI validation suite:
      1. Pipeline & secret scanner tests (Python)
      2. Locale key & placeholder validation
      3. YOLOX model provenance, license & checksum assertion
      4. NuGet dependency restore
      5. Code formatting verification (dotnet format)
      6. Release build with -warnaserror
      7. Unit and UI regression tests with coverage collection
      8. Release security gates validation
      9. Update compatibility assertion against 1.9.3 client
      10. Package vulnerability audit
      11. Enforces 70% application line coverage

.PARAMETER FixFormat
    Automatically formats code with 'dotnet format' before verifying.

.PARAMETER SkipRestore
    Skips the 'dotnet restore' step to save time if dependencies are unchanged.

.PARAMETER Fast
    Runs validation without code coverage collection or its threshold check.

.PARAMETER IncludeDesktopIntegration
    Includes tests that require visible desktop windows, in addition to the CI suite.

.PARAMETER MinimumCoverage
    Minimum code coverage percentage required (default: 70).
#>
[CmdletBinding()]
param(
    [switch]$FixFormat,
    [switch]$SkipRestore,
    [switch]$Fast,
    [switch]$IncludeDesktopIntegration,
    [ValidateRange(0, 100)][decimal]$MinimumCoverage = 70
)

Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process -Force
$ErrorActionPreference = 'Stop'

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$repository = Split-Path $PSScriptRoot -Parent
$previousQaDirectory = [Environment]::GetEnvironmentVariable('VNOTCH_QA_ARTIFACT_DIR', 'Process')
$previousDesktopMode = [Environment]::GetEnvironmentVariable('VNOTCH_RUN_DESKTOP_TESTS', 'Process')
Push-Location $repository
try {

Write-Host "`n========================================================" -ForegroundColor Cyan
Write-Host "        V-Notch Full CI Local Verification               " -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan

# 0. Stop old instances to avoid file lock issues during Release build
$processName = "V-Notch"
$running = Get-Process -Name $processName -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "`n>>> Stopping running V-Notch instances..." -ForegroundColor Yellow
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    taskkill /F /IM "$processName.exe" /T 2>$null | Out-Null
    Start-Sleep -Milliseconds 400
}

# Keep each run's evidence, including failures, separate from previous runs.
$artifactsDir = Join-Path $repository 'artifacts'
$runDirectory = Join-Path $artifactsDir ('local-ci-' + [guid]::NewGuid().ToString('N'))
$testResultsDir = Join-Path $runDirectory 'test-results'
$uiReviewDir = Join-Path $runDirectory 'ui-review'
New-Item -ItemType Directory -Path $testResultsDir -Force | Out-Null
New-Item -ItemType Directory -Path $uiReviewDir -Force | Out-Null
$env:VNOTCH_QA_ARTIFACT_DIR = $uiReviewDir
$env:VNOTCH_RUN_DESKTOP_TESTS = if ($IncludeDesktopIntegration) { '1' } else { '0' }
Write-Host "Evidence directory: $runDirectory"

# Prune older local-ci-* runs to prevent disk accumulation (keep last 3)
$oldRuns = @(Get-ChildItem -LiteralPath $artifactsDir -Directory -Filter 'local-ci-*' -ErrorAction SilentlyContinue |
    Sort-Object CreationTime -Descending | Select-Object -Skip 3)
foreach ($old in $oldRuns) {
    Remove-Item -LiteralPath $old.FullName -Recurse -Force -ErrorAction SilentlyContinue
}

# Step 1: Pipeline & Locale Validation (Python)
Write-Host "`n=== [1/10] Validating Locales and Pipeline Scripts ===" -ForegroundColor Cyan
$python = Get-Command python -ErrorAction SilentlyContinue
if ($python) {
    Write-Host ">>> Running test_pipeline_checks.py..." -ForegroundColor Gray
    & python -m unittest discover -s "$repository/.github/scripts" -p "test_*.py"
    if ($LASTEXITCODE -ne 0) { throw "Pipeline tests failed." }

    Write-Host ">>> Running validate_locales.py..." -ForegroundColor Gray
    & python "$repository/Tools/validate_locales.py"
    if ($LASTEXITCODE -ne 0) { throw "Locale validation failed." }
} else {
    throw 'Python is required for pipeline tests and locale validation.'
}

# Step 2: Model Assets Verification
Write-Host "`n=== [2/10] Verifying Model Asset Provenance & License ===" -ForegroundColor Cyan
& "$repository/Tools/Assert-ModelAssets.ps1"

# Step 3: Restore Dependencies
Write-Host "`n=== [3/10] Restoring Solution Dependencies ===" -ForegroundColor Cyan
if (-not $SkipRestore) {
    & dotnet restore "$repository/V-Notch.sln" --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked dependency restore failed.' }
    Write-Host "Dependencies restored successfully." -ForegroundColor Green
} else {
    Write-Host ">>> -SkipRestore passed: skipping dependency restore." -ForegroundColor Yellow
}

# Step 4: Code Formatting Verification
Write-Host "`n=== [4/10] Verifying Code Formatting ===" -ForegroundColor Cyan
if ($FixFormat) {
    Write-Host ">>> -FixFormat passed: auto-formatting code with 'dotnet format'..." -ForegroundColor Yellow
    & dotnet format "$repository/V-Notch.sln"
    if ($LASTEXITCODE -ne 0) { throw 'Automatic formatting failed.' }
}
& dotnet format "$repository/V-Notch.sln" --verify-no-changes --no-restore
if ($LASTEXITCODE -ne 0) {
    Write-Host "`n[!] Code format verification failed!" -ForegroundColor Red
    Write-Host "[*] Run 'dotnet format V-Notch.sln' or '.\scripts\c.ps1 -FixFormat' to auto-fix formatting." -ForegroundColor Yellow
    throw "Code formatting verification failed."
}
Write-Host "Code formatting check passed." -ForegroundColor Green

# Step 5: Build Solution in Release Mode
Write-Host "`n=== [5/10] Building Solution (Release + WarnAsError) ===" -ForegroundColor Cyan
& dotnet build "$repository/V-Notch.sln" --configuration Release --no-restore -warnaserror
if ($LASTEXITCODE -ne 0) { throw "Build failed with warnings or errors." }
Write-Host "Release build succeeded with 0 warnings and 0 errors." -ForegroundColor Green

# Step 6: Run Unit & UI Tests
Write-Host "`n=== [6/10] Running Unit & UI Regression Tests ===" -ForegroundColor Cyan
$testArguments = @(
    'test', "$repository/Tests/VNotch.Tests.csproj", '--configuration', 'Release', '--no-build', '--no-restore',
    '--logger', 'trx;LogFileName=tests.trx', '--results-directory', $testResultsDir,
    '--verbosity', 'normal', '--blame-hang-timeout', '2m', '--blame-hang-dump-type', 'mini'
)
if (-not $IncludeDesktopIntegration) {
    Write-Host ">>> Running tests in headless mode (skipping DesktopIntegration)..." -ForegroundColor Yellow
    $testArguments += @('--filter', 'Category!=DesktopIntegration')
} else {
    Write-Host ">>> Running all tests including DesktopIntegration..." -ForegroundColor Yellow
}
if ($Fast) {
    Write-Host ">>> -Fast mode: Running tests without coverage collection..." -ForegroundColor Yellow
} else {
    $testArguments += @('--settings', "$repository/Tests/CI.runsettings", '--collect', 'Code Coverage')
}
& dotnet @testArguments
if ($LASTEXITCODE -ne 0) { throw "Test run failed." }
Write-Host "All tests passed successfully." -ForegroundColor Green

# Step 7: Release Security Gates
Write-Host "`n=== [7/10] Testing Release Security Gates ===" -ForegroundColor Cyan
& "$repository/Tests/ReleaseGateTests.ps1"
# Run this fixture in a child process: it stubs dotnet and executes an exit-based runner.
& (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File "$repository/Tests/PrepushCleanupTests.ps1"
if ($LASTEXITCODE -ne 0) { throw 'Prepush cleanup regression tests failed.' }

# Step 8: Update Compatibility Assertion
Write-Host "`n=== [8/10] Verifying Update Compatibility with 1.9.3 Client ===" -ForegroundColor Cyan
& "$repository/Tools/Assert-UpdateCompatibility.ps1"

# Step 9: Package Audit
Write-Host "`n=== [9/10] Auditing Packages for Known Vulnerabilities ===" -ForegroundColor Cyan
& "$repository/Tools/Assert-PackageAudit.ps1" -Solution "$repository/V-Notch.sln" -OutputPath "$runDirectory/package-audit.json"

# Step 10: Enforce Application Code Coverage
Write-Host "`n=== [10/10] Enforcing Minimum Code Coverage ($MinimumCoverage%) ===" -ForegroundColor Cyan
if (-not $Fast) {
    & "$repository/Tools/Assert-Coverage.ps1" -ResultsDirectory $testResultsDir -MinimumPercent $MinimumCoverage
} else {
    Write-Host ">>> -Fast mode: skipping coverage threshold check." -ForegroundColor Yellow
}

$stopwatch.Stop()
$elapsed = $stopwatch.Elapsed

Write-Host "`n========================================================" -ForegroundColor Green
Write-Host "   LOCAL CHECKS PASSED IN $($elapsed.ToString("mm\:ss"))!  " -ForegroundColor Green
Write-Host "   Coverage enforced: $(-not $Fast); Desktop integration: $IncludeDesktopIntegration" -ForegroundColor Green
Write-Host "   Evidence: $runDirectory" -ForegroundColor Green
Write-Host "========================================================`n" -ForegroundColor Green
} finally {
    [Environment]::SetEnvironmentVariable('VNOTCH_QA_ARTIFACT_DIR', $previousQaDirectory, 'Process')
    [Environment]::SetEnvironmentVariable('VNOTCH_RUN_DESKTOP_TESTS', $previousDesktopMode, 'Process')
    Pop-Location
}
