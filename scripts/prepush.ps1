# Pre-push CI verification script for V-Notch
# Mirrors .github/workflows/ci.yml locally

param(
    [switch]$FixFormat,
    [switch]$IncludeDesktopIntegration
)

$ErrorActionPreference = "Stop"
Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process -Force
$repository = Split-Path $PSScriptRoot -Parent

Write-Host "`n=== [1/5] Restoring Solution Dependencies ===" -ForegroundColor Cyan
dotnet restore "$repository/V-Notch.sln"
if ($LASTEXITCODE -ne 0) {
    Write-Host "[!] Restore failed." -ForegroundColor Red
    exit 1
}

Write-Host "`n=== [2/5] Verifying Code Formatting ===" -ForegroundColor Cyan
if ($FixFormat) {
    Write-Host ">>> -FixFormat passed: formatting code first..." -ForegroundColor Yellow
    dotnet format "$repository/V-Notch.sln"
}

dotnet format "$repository/V-Notch.sln" --verify-no-changes --no-restore
if ($LASTEXITCODE -ne 0) {
    Write-Host "`n[!] Code format verification failed!" -ForegroundColor Red
    Write-Host "[*] Run 'dotnet format V-Notch.sln' or '.\scripts\prepush.ps1 -FixFormat' to auto-fix formatting." -ForegroundColor Yellow
    exit 1
}
Write-Host "Formatting check passed." -ForegroundColor Green

Write-Host "`n=== [3/5] Building Solution (Release + WarnAsError) ===" -ForegroundColor Cyan
dotnet build "$repository/V-Notch.sln" --configuration Release --no-restore -warnaserror
if ($LASTEXITCODE -ne 0) {
    Write-Host "`n[!] Build failed with warnings or errors." -ForegroundColor Red
    exit 1
}
Write-Host "Build succeeded with 0 warnings and 0 errors." -ForegroundColor Green

Write-Host "`n=== [4/5] Running Tests & Collecting Coverage ===" -ForegroundColor Cyan
$coverageResults = Join-Path $repository ('artifacts/prepush-coverage-' + [guid]::NewGuid().ToString('N'))
try {
    if (-not $IncludeDesktopIntegration) {
        Write-Host ">>> Running tests in headless mode (skipping DesktopIntegration window popups)..." -ForegroundColor Yellow
        dotnet test "$repository/Tests/VNotch.Tests.csproj" --configuration Release --no-build --no-restore --filter "Category!=DesktopIntegration" --settings "$repository/Tests/CI.runsettings" --collect "Code Coverage" --results-directory $coverageResults --verbosity normal
    } else {
        Write-Host ">>> Running all tests including DesktopIntegration..." -ForegroundColor Yellow
        $previousDesktopTestMode = [Environment]::GetEnvironmentVariable('VNOTCH_RUN_DESKTOP_TESTS', 'Process')
        try {
            $env:VNOTCH_RUN_DESKTOP_TESTS = '1'
            dotnet test "$repository/Tests/VNotch.Tests.csproj" --configuration Release --no-build --no-restore --settings "$repository/Tests/CI.runsettings" --collect "Code Coverage" --results-directory $coverageResults --verbosity normal
        } finally {
            [Environment]::SetEnvironmentVariable('VNOTCH_RUN_DESKTOP_TESTS', $previousDesktopTestMode, 'Process')
        }
    }
    if ($LASTEXITCODE -ne 0) {
        throw 'Test run failed.'
    }

    # Use the same assembly scope and threshold as c.ps1 and GitHub Actions.
    & "$repository/Tools/Assert-Coverage.ps1" -ResultsDirectory $coverageResults
    Write-Host "Test & coverage check passed." -ForegroundColor Green
} finally {
    $coverageRoot = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
    $coverageTarget = [IO.Path]::GetFullPath($coverageResults)
    if (-not [string]::Equals([IO.Path]::GetDirectoryName($coverageTarget), $coverageRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Coverage cleanup target must be inside the repository artifacts directory.'
    }
    if (Test-Path -LiteralPath $coverageTarget) {
        Remove-Item -LiteralPath $coverageTarget -Recurse -Force
    }
}

Write-Host "`n=== [5/5] Auditing NuGet Packages for Vulnerabilities ===" -ForegroundColor Cyan
dotnet list "$repository/V-Notch.sln" package --vulnerable --include-transitive
if ($LASTEXITCODE -ne 0) {
    Write-Host "`n[!] Vulnerable packages detected." -ForegroundColor Red
    exit 1
}

Write-Host "`n=======================================================" -ForegroundColor Green
Write-Host " ALL CI PRE-PUSH CHECKS PASSED SUCCESSFULLY!" -ForegroundColor Green
Write-Host " Safe to push to remote." -ForegroundColor Green
Write-Host "=======================================================`n" -ForegroundColor Green
exit 0
