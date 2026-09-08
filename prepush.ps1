# Pre-push CI verification script for V-Notch
# Mirrors .github/workflows/ci.yml locally

param(
    [switch]$FixFormat,
    [switch]$IncludeDesktopIntegration
)

$ErrorActionPreference = "Stop"
Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process -Force

Write-Host "`n=== [1/5] Restoring Solution Dependencies ===" -ForegroundColor Cyan
dotnet restore V-Notch.sln
if ($LASTEXITCODE -ne 0) {
    Write-Host "[!] Restore failed." -ForegroundColor Red
    exit 1
}

Write-Host "`n=== [2/5] Verifying Code Formatting ===" -ForegroundColor Cyan
if ($FixFormat) {
    Write-Host ">>> -FixFormat passed: formatting code first..." -ForegroundColor Yellow
    dotnet format V-Notch.sln
}

dotnet format V-Notch.sln --verify-no-changes --no-restore
if ($LASTEXITCODE -ne 0) {
    Write-Host "`n[!] Code format verification failed!" -ForegroundColor Red
    Write-Host "[*] Run 'dotnet format V-Notch.sln' or '.\prepush.ps1 -FixFormat' to auto-fix formatting." -ForegroundColor Yellow
    exit 1
}
Write-Host "Formatting check passed." -ForegroundColor Green

Write-Host "`n=== [3/5] Building Solution (Release + WarnAsError) ===" -ForegroundColor Cyan
dotnet build V-Notch.sln --configuration Release --no-restore -warnaserror
if ($LASTEXITCODE -ne 0) {
    Write-Host "`n[!] Build failed with warnings or errors." -ForegroundColor Red
    exit 1
}
Write-Host "Build succeeded with 0 warnings and 0 errors." -ForegroundColor Green

Write-Host "`n=== [4/5] Running Tests & Collecting Coverage ===" -ForegroundColor Cyan
if (-not $IncludeDesktopIntegration) {
    Write-Host ">>> Running tests in headless mode (skipping DesktopIntegration window popups)..." -ForegroundColor Yellow
    dotnet test Tests/VNotch.Tests.csproj --configuration Release --no-build --no-restore --filter "Category!=DesktopIntegration" --collect:"Code Coverage;Format=Cobertura" --results-directory Tests/artifacts/coverage/ --verbosity normal
} else {
    Write-Host ">>> Running all tests including DesktopIntegration..." -ForegroundColor Yellow
    dotnet test Tests/VNotch.Tests.csproj --configuration Release --no-build --no-restore --collect:"Code Coverage;Format=Cobertura" --results-directory Tests/artifacts/coverage/ --verbosity normal
}
if ($LASTEXITCODE -ne 0) {
    Write-Host "`n[!] Test run failed." -ForegroundColor Red
    exit 1
}

# Coverage validation matching GitHub Actions CI
$coverageFile = Get-ChildItem -Path Tests/artifacts/coverage -Filter *.cobertura.xml -Recurse | Where-Object { $_.Name -ne 'coverage.cobertura.xml' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($coverageFile) {
    Copy-Item $coverageFile.FullName -Destination Tests/artifacts/coverage/coverage.cobertura.xml -Force
}

if (-not (Test-Path Tests/artifacts/coverage/coverage.cobertura.xml)) {
    Write-Host "`n[!] Coverage report file not found." -ForegroundColor Red
    exit 1
}

[xml]$coverage = Get-Content Tests/artifacts/coverage/coverage.cobertura.xml
$lineRate = [double]$coverage.coverage.'line-rate'
Write-Host ("Coverage line-rate: {0:P2}" -f $lineRate) -ForegroundColor Cyan

if ($lineRate -le 0) {
    Write-Host "`n[!] Coverage report is empty." -ForegroundColor Red
    exit 1
}

$baseline = 'Tests/coverage-baseline.txt'
if (Test-Path $baseline) {
    $minimum = [double](Get-Content $baseline -Raw)
    Write-Host ("Baseline minimum: {0:P2}" -f $minimum) -ForegroundColor Gray
    if ($lineRate -lt $minimum) {
        Write-Host "`n[!] Coverage dropped below baseline $minimum." -ForegroundColor Red
        exit 1
    }
}
Write-Host "Test & coverage check passed." -ForegroundColor Green

Write-Host "`n=== [5/5] Auditing NuGet Packages for Vulnerabilities ===" -ForegroundColor Cyan
dotnet list V-Notch.sln package --vulnerable --include-transitive
if ($LASTEXITCODE -ne 0) {
    Write-Host "`n[!] Vulnerable packages detected." -ForegroundColor Red
    exit 1
}

Write-Host "`n=======================================================" -ForegroundColor Green
Write-Host " ALL CI PRE-PUSH CHECKS PASSED SUCCESSFULLY!" -ForegroundColor Green
Write-Host " Safe to push to remote." -ForegroundColor Green
Write-Host "=======================================================`n" -ForegroundColor Green
exit 0
