<#
.SYNOPSIS
    Full CI Verification Script for V-Notch.
    Mirrors .github/workflows/ci.yml locally before pushing.
    Hosted CodeQL analysis runs in GitHub Actions (check_sarif.py and BuildNativeDll are verified locally).

.DESCRIPTION
    Runs the complete Windows CI validation suite matching .github/workflows/ci.yml:
 *       1. Pipeline gates, Gitleaks 8.30.1 secret scan, CI scope & locale validation (Python + Gitleaks)
 *       2. Model asset provenance, license & checksum assertion
 *       3. NuGet dependency restore (--locked-mode)
 *       4. Native C++26 DLL & Release build with -warnaserror
 *       5. Unit tests (--blame-hang-timeout 60s)
 *       6. Release security gates & runner regression tests
 *       7. Update compatibility (1.9.3 client & beta-to-stable promotion)
 *       8. NuGet package vulnerability audit

.PARAMETER Resume
    Evidence directory of a failed run. Resumes at its first unfinished step.
    Reuse the same validation options as the original run.
    If a fix changes compiled code, rebuild Release before resuming the test step.

.PARAMETER FixFormat
    Optionally formats changed C#/MSBuild files with 'dotnet format whitespace' before building.

.PARAMETER SkipRestore
    Skips the 'dotnet restore' step to save time if dependencies are unchanged.

.PARAMETER Fast
    Retained for CLI/checkpoint option compatibility.

.PARAMETER IncludeDesktopIntegration
    Includes tests that require visible desktop windows, in addition to the headless CI suite.

.PARAMETER MinimumCoverage
    Minimum code coverage percentage option retained for CLI/checkpoint compatibility (default: 70).
#>
[CmdletBinding()]
param(
    [switch]$FixFormat,
    [string]$Resume,
    [switch]$SkipRestore,
    [switch]$Fast,
    [switch]$IncludeDesktopIntegration,
    [ValidateRange(0, 100)][decimal]$MinimumCoverage = 70
)

Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process -Force
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ge 7) {
    $PSNativeCommandUseErrorActionPreference = $false
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$env:GIT_TERMINAL_PROMPT = '0'
$env:GCM_INTERACTIVE = 'never'
$env:Path = ($env:Path -split ';' | Where-Object { $_ -ne 'C:\Program Files\Git\mingw64\bin' }) -join ';'

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition }
$repository = Split-Path $scriptDir -Parent
$previousQaDirectory = [Environment]::GetEnvironmentVariable('VNOTCH_QA_ARTIFACT_DIR', 'Process')
$previousDesktopMode = [Environment]::GetEnvironmentVariable('VNOTCH_RUN_DESKTOP_TESTS', 'Process')
$previousWinTarget   = [Environment]::GetEnvironmentVariable('EnableWindowsTargeting', 'Process')
$previousSkipFirst   = [Environment]::GetEnvironmentVariable('DOTNET_SKIP_FIRST_TIME_EXPERIENCE', 'Process')
$previousNoLogo      = [Environment]::GetEnvironmentVariable('DOTNET_NOLOGO', 'Process')
$previousTelemetry   = [Environment]::GetEnvironmentVariable('DOTNET_CLI_TELEMETRY_OPTOUT', 'Process')
$previousPyBytecode  = [Environment]::GetEnvironmentVariable('PYTHONDONTWRITEBYTECODE', 'Process')
$env:EnableWindowsTargeting = 'true'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:PYTHONDONTWRITEBYTECODE = '1'

$gTopLeft = [char]0x256D
$gBotLeft = [char]0x2570
$gVert    = [char]0x2502
$gBranch  = [char]0x251C
$gHoriz   = [char]0x2500
$gDiamond = [char]0x25C6
$gCheck   = [char]0x2714
$gCross   = [char]0x2716
$gWarn    = [char]0x25B2
$gArrow   = [char]0x25B8
$lineRule = ([string]$gHoriz) * 56

function Write-TreeStep([string]$Tag, [string]$Title) {
    Write-Host "  $gBranch$gHoriz " -ForegroundColor DarkCyan -NoNewline
    Write-Host "[$Tag] " -ForegroundColor Cyan -NoNewline
    Write-Host $Title -ForegroundColor White
}

function Write-TreeItem([string]$Message, [string]$State = "Info") {
    switch ($State) {
        "Info"    { $sym = $gArrow; $sCol = "DarkCyan"; $tCol = "Gray" }
        "Success" { $sym = $gCheck; $sCol = "Green";    $tCol = "Gray" }
        "Warn"    { $sym = $gWarn;  $sCol = "Yellow";   $tCol = "Yellow" }
        "Error"   { $sym = $gCross; $sCol = "Red";      $tCol = "Red" }
    }
    Write-Host "  $gVert     " -ForegroundColor DarkCyan -NoNewline
    Write-Host "$sym " -ForegroundColor $sCol -NoNewline
    Write-Host $Message -ForegroundColor $tCol
}

function Write-TreeSpacer {
    Write-Host "  $gVert" -ForegroundColor DarkCyan
}

Push-Location $repository
try {

# Keep each run's evidence, including failures, separate from previous runs.
$artifactsDir = Join-Path $repository 'artifacts'
$options = "$SkipRestore|$Fast|$IncludeDesktopIntegration|$MinimumCoverage"
$startStep = 1
$testResultsDir = $null
if ($Resume) {
    $runDirectory = (Resolve-Path -LiteralPath $Resume).Path
    $checkpoint = Get-Content -LiteralPath (Join-Path $runDirectory 'checkpoint.json') -Raw | ConvertFrom-Json
    if ($checkpoint.Repository -ne $repository -or $checkpoint.Options -ne $options) {
        throw 'Resume requires the same repository and validation options as the original run.'
    }
    $startStep = [int]$checkpoint.NextStep
    if ($startStep -lt 1 -or $startStep -gt 9) { throw 'Invalid checkpoint step.' }
    $testResultsDir = $checkpoint.TestResultsDirectory
} else {
    if (-not $env:VNOTCH_CI_RESUME_TEST_FIXTURE -and (Test-Path -LiteralPath $artifactsDir)) {
        Get-ChildItem -LiteralPath $artifactsDir -Directory -Filter 'local-ci-*' -ErrorAction SilentlyContinue |
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    }
    $runDirectory = Join-Path $artifactsDir ('local-ci-' + [guid]::NewGuid().ToString('N'))
}
if (-not $testResultsDir) { $testResultsDir = Join-Path $runDirectory 'test-results' }
$uiReviewDir = Join-Path $runDirectory 'ui-review'
New-Item -ItemType Directory -Path $testResultsDir -Force | Out-Null
New-Item -ItemType Directory -Path $uiReviewDir -Force | Out-Null
$env:VNOTCH_QA_ARTIFACT_DIR = $uiReviewDir
$env:VNOTCH_RUN_DESKTOP_TESTS = if ($IncludeDesktopIntegration) { '1' } else { '0' }

function Invoke-CapturedNative([scriptblock]$Command) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Command 2>&1
    } finally {
        $ErrorActionPreference = $prev
    }
}

function Invoke-StepScript([scriptblock]$Command) {
    $global:LASTEXITCODE = 0
    $out = [Collections.Generic.List[string]]::new()
    try {
        & $Command *>&1 | ForEach-Object {
            $line = "$_".Trim()
            if ($line) { $out.Add($line) }
        }
        if ($LASTEXITCODE -ne 0) {
            foreach ($l in $out) { Write-TreeItem $l -State "Error" }
            throw "Check failed with exit code $LASTEXITCODE."
        }
    } catch {
        foreach ($l in $out) { Write-TreeItem $l -State "Error" }
        Write-TreeItem $_.Exception.Message -State "Error"
        throw
    }
}

function Save-Checkpoint([int]$nextStep) {
    @{ Repository = $repository; Options = $options; NextStep = $nextStep; TestResultsDirectory = $testResultsDir } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDirectory 'checkpoint.json') -Encoding utf8
}
Save-Checkpoint $startStep

$relRunDir = if ($runDirectory.StartsWith($repository)) { $runDirectory.Substring($repository.Length + 1) } else { $runDirectory }

Write-Host ""
Write-Host "  $gTopLeft$lineRule" -ForegroundColor DarkCyan
Write-Host "  $gVert  " -ForegroundColor DarkCyan -NoNewline
Write-Host "$gDiamond V-NOTCH LOCAL CI VERIFICATION" -ForegroundColor Cyan -NoNewline
Write-Host "  $gVert  " -ForegroundColor DarkGray -NoNewline
Write-Host ("Step $startStep..8") -ForegroundColor Green
Write-Host "  $gVert     " -ForegroundColor DarkCyan -NoNewline
Write-Host "$gArrow Evidence : $relRunDir" -ForegroundColor DarkGray
Write-TreeSpacer

if ($startStep -le 1) {
    Write-TreeStep "1/8" "Pipeline, Secret Scanner & Locales (Python + Gitleaks)"
    $python = Get-Command python -ErrorAction SilentlyContinue
    if ($python) {
        $pyOut1 = Invoke-CapturedNative { & python -m unittest discover -s "$repository/.github/scripts" -p "test_*.py" }
        if ($LASTEXITCODE -ne 0) {
            foreach ($l in @($pyOut1 | ForEach-Object { "$_" } | Where-Object { $_.Trim() })) { Write-TreeItem $l -State "Error" }
            throw "Pipeline tests failed."
        }
        Write-TreeItem "Pipeline unit tests passed." -State "Success"

        if (-not $env:VNOTCH_CI_RESUME_TEST_FIXTURE) {
            $gitleaksCache = Join-Path $repository '.cache\gitleaks'
            $gitleaksZip = Join-Path $gitleaksCache 'gitleaks_8.30.1_windows_x64.zip'
            $gitleaksExe = Join-Path $gitleaksCache 'gitleaks.exe'
            $expectedGitleaksSha = 'd29144deff3a68aa93ced33dddf84b7fdc26070add4aa0f4513094c8332afc4e'
            if (-not (Test-Path -LiteralPath $gitleaksExe)) {
                New-Item -ItemType Directory -Path $gitleaksCache -Force | Out-Null
                if (-not (Test-Path -LiteralPath $gitleaksZip)) {
                    Invoke-WebRequest -Uri 'https://github.com/gitleaks/gitleaks/releases/download/v8.30.1/gitleaks_8.30.1_windows_x64.zip' -OutFile $gitleaksZip -UseBasicParsing
                }
                $actualGitleaksSha = (Get-FileHash -LiteralPath $gitleaksZip -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($actualGitleaksSha -ne $expectedGitleaksSha) {
                    Remove-Item -LiteralPath $gitleaksZip -Force -ErrorAction SilentlyContinue
                    throw 'Gitleaks 8.30.1 archive SHA-256 verification failed.'
                }
                Expand-Archive -LiteralPath $gitleaksZip -DestinationPath $gitleaksCache -Force
                Remove-Item -LiteralPath $gitleaksZip -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath (Join-Path $gitleaksCache 'LICENSE'), (Join-Path $gitleaksCache 'README.md') -Force -ErrorAction SilentlyContinue
            }
            $scanGateOut = Invoke-CapturedNative { & python "$repository/.github/scripts/check_secret_scanner.py" $gitleaksExe }
            if ($LASTEXITCODE -ne 0) {
                foreach ($l in @($scanGateOut | ForEach-Object { "$_" } | Where-Object { $_.Trim() })) { Write-TreeItem $l -State "Error" }
                throw "Secret scanner gate failed."
            }
            $sarifPath = Join-Path $runDirectory 'gitleaks.sarif'
            $glOut = Invoke-CapturedNative {
                & $gitleaksExe git $repository --log-opts=--all --config "$repository/.gitleaks.toml" `
                    --gitleaks-ignore-path "$repository/.gitleaksignore" --redact=100 --no-banner `
                    --report-format sarif --report-path $sarifPath
            }
            if ($LASTEXITCODE -ne 0) {
                foreach ($l in @($glOut | ForEach-Object { "$_" } | Where-Object { $_.Trim() })) { Write-TreeItem $l -State "Error" }
                throw "Gitleaks history secret scan failed."
            }
            Write-TreeItem "Secret scanner & Gitleaks 8.30.1 history scan passed." -State "Success"

            $scopeOutput = Join-Path $runDirectory 'ci-scope.txt'
            $prevGhOut = [Environment]::GetEnvironmentVariable('GITHUB_OUTPUT', 'Process')
            $prevBaseSha = [Environment]::GetEnvironmentVariable('BASE_SHA', 'Process')
            $prevHeadSha = [Environment]::GetEnvironmentVariable('HEAD_SHA', 'Process')
            $prevGitRef = [Environment]::GetEnvironmentVariable('GIT_REF', 'Process')
            try {
                $env:GITHUB_OUTPUT = $scopeOutput
                $env:BASE_SHA = ('0' * 40)
                $env:HEAD_SHA = [string](& git -C $repository rev-parse HEAD 2>$null).Trim()
                $env:GIT_REF = [string](& git -C $repository symbolic-ref HEAD 2>$null).Trim()
                if (-not $env:GIT_REF) { $env:GIT_REF = 'refs/heads/main' }
                $scopeOut = Invoke-CapturedNative { & python "$repository/.github/scripts/ci_scope.py" scope }
                if ($LASTEXITCODE -ne 0) {
                    foreach ($l in @($scopeOut | ForEach-Object { "$_" } | Where-Object { $_.Trim() })) { Write-TreeItem $l -State "Error" }
                    throw "CI scope verification failed."
                }
            } finally {
                [Environment]::SetEnvironmentVariable('GITHUB_OUTPUT', $prevGhOut, 'Process')
                [Environment]::SetEnvironmentVariable('BASE_SHA', $prevBaseSha, 'Process')
                [Environment]::SetEnvironmentVariable('HEAD_SHA', $prevHeadSha, 'Process')
                [Environment]::SetEnvironmentVariable('GIT_REF', $prevGitRef, 'Process')
            }
        }

        $pyOut2 = Invoke-CapturedNative { & python "$repository/Tools/validate_locales.py" }
        if ($LASTEXITCODE -ne 0) {
            foreach ($l in @($pyOut2 | ForEach-Object { "$_" } | Where-Object { $_.Trim() })) { Write-TreeItem $l -State "Error" }
            throw "Locale validation failed."
        }
        Write-TreeItem "All locale JSON dictionaries validated." -State "Success"
    } else {
        throw 'Python is required for pipeline tests and locale validation.'
    }

    Save-Checkpoint 2
    Write-TreeSpacer
}

if ($startStep -le 2) {
    Write-TreeStep "2/8" "Model Asset Provenance & License"
    Invoke-StepScript { & "$repository/Tools/Assert-ModelAssets.ps1" } | Out-Null
    Write-TreeItem "Verified YOLOX model checksum & Apache-2.0 license." -State "Success"

    Save-Checkpoint 3
    Write-TreeSpacer
}

if ($startStep -le 3) {
    Write-TreeStep "3/8" "NuGet Dependency Restore"
    if (-not $SkipRestore) {
        $assetsReady = (-not $env:VNOTCH_CI_RESUME_TEST_FIXTURE) -and
            (Test-Path -LiteralPath (Join-Path $repository 'obj\project.assets.json')) -and
            (Test-Path -LiteralPath (Join-Path $repository 'Tests\obj\project.assets.json')) -and
            (Test-Path -LiteralPath (Join-Path $repository 'Uninstall\obj\project.assets.json')) -and
            ((Get-Item -LiteralPath (Join-Path $repository 'obj\project.assets.json')).LastWriteTimeUtc -ge (Get-Item -LiteralPath (Join-Path $repository 'packages.lock.json')).LastWriteTimeUtc) -and
            ((Get-Item -LiteralPath (Join-Path $repository 'Tests\obj\project.assets.json')).LastWriteTimeUtc -ge (Get-Item -LiteralPath (Join-Path $repository 'Tests\packages.lock.json')).LastWriteTimeUtc) -and
            ((Get-Item -LiteralPath (Join-Path $repository 'Uninstall\obj\project.assets.json')).LastWriteTimeUtc -ge (Get-Item -LiteralPath (Join-Path $repository 'Uninstall\packages.lock.json')).LastWriteTimeUtc)
        if ($assetsReady) {
            Write-TreeItem "Dependencies already up-to-date with lockfiles." -State "Success"
        } else {
            $restOut = Invoke-CapturedNative { & dotnet restore "$repository/V-Notch.sln" --locked-mode -nologo -v:q }
            if ($LASTEXITCODE -ne 0) {
                foreach ($l in @($restOut | ForEach-Object { "$_" } | Where-Object { $_.Trim() })) { Write-TreeItem $l -State "Error" }
                throw 'Locked dependency restore failed.'
            }
            Write-TreeItem "Dependencies restored (--locked-mode)." -State "Success"
        }
    } else {
        Write-TreeItem "Skipped (-SkipRestore)." -State "Warn"
    }

    Save-Checkpoint 4
    Write-TreeSpacer
}

if ($startStep -le 4) {
    Write-TreeStep "4/8" "Native C++26 DLL & Release Build (-warnaserror)"
    if ($FixFormat) {
        Write-TreeItem "Auto-formatting with 'dotnet format whitespace' (-FixFormat)..." -State "Info"
        Invoke-CapturedNative { & dotnet format whitespace "$repository/V-Notch.sln" --no-restore } | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Automatic formatting failed.' }
    }
    $bldSw = [System.Diagnostics.Stopwatch]::StartNew()
    $bldOut = Invoke-CapturedNative { & dotnet build "$repository/V-Notch.sln" --configuration Release --no-restore -warnaserror -nologo -v:q -clp:NoSummary -m -p:NativeFastCompile=true }
    $bldSw.Stop()
    if ($LASTEXITCODE -ne 0) {
        foreach ($l in @($bldOut | ForEach-Object { "$_" } | Where-Object { $_.Trim() })) { Write-TreeItem $l -State "Error" }
        throw "Build failed with warnings or errors."
    }
    if (-not $env:VNOTCH_CI_RESUME_TEST_FIXTURE -and -not (Test-Path -LiteralPath (Join-Path $repository 'Native\out\VNotch.Native.dll'))) {
        Write-TreeItem "Native/out/VNotch.Native.dll was not found after build." -State "Error"
        throw "Failed to compile VNotch.Native.dll."
    }
    Write-TreeItem ("Release build succeeded (0 warnings, 0 errors) in {0:N1}s." -f $bldSw.Elapsed.TotalSeconds) -State "Success"

    Save-Checkpoint 5
    Write-TreeSpacer
}

if ($startStep -le 5) {
    Write-TreeStep "5/8" "Unit Tests"
    if ($Resume -and $startStep -le 5) {
        $testResultsDir = Join-Path $runDirectory ('test-results-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $testResultsDir -Force | Out-Null
        Save-Checkpoint 5
    }
    $coverageResultsDir = Join-Path $repository 'TestResults\Coverage'
    if (Test-Path -LiteralPath $coverageResultsDir) {
        Remove-Item -LiteralPath $coverageResultsDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    $testArgs = @(
        'test', "$repository/Tests/VNotch.Tests.csproj",
        '--configuration', 'Release', '--no-build', '--no-restore'
    )
    if (-not $IncludeDesktopIntegration) {
        $testArgs += @('--filter', 'Category!=DesktopIntegration')
    }
    $testArgs += @(
        '--blame-hang-timeout', '60s',
        '--collect:XPlat Code Coverage',
        '--results-directory', $coverageResultsDir,
        '--logger', 'trx;LogFileName=unit-tests.trx',
        '--logger', 'console;verbosity=minimal'
    )
    $tstSw = [System.Diagnostics.Stopwatch]::StartNew()
    $tstOut = Invoke-CapturedNative { & dotnet @testArgs }
    $tstSw.Stop()
    if ($LASTEXITCODE -ne 0) {
        foreach ($l in @($tstOut | ForEach-Object { "$_" } | Where-Object { $_.Trim() })) { Write-TreeItem $l -State "Error" }
        throw "Test run failed."
    }
    if ($MinimumCoverage -gt 0) {
        Invoke-StepScript { & "$repository/Tools/Assert-Coverage.ps1" -ResultsDirectory $coverageResultsDir -MinimumLinePercent $MinimumCoverage } | Out-Null
    }
    Write-TreeItem ("Unit tests passed in {0:N1}s." -f $tstSw.Elapsed.TotalSeconds) -State "Success"

    Save-Checkpoint 6
    Write-TreeSpacer
}

if ($startStep -le 6) {
    Write-TreeStep "6/8" "Release Security Gates"
    Invoke-StepScript { & "$repository/Tests/ReleaseGateTests.ps1" } | Out-Null
    Invoke-StepScript { & "$repository/Tests/LocalCiResumeTests.ps1" } | Out-Null
    Write-TreeItem "Coverage boundaries, XML safety, signing & runner regression gates passed." -State "Success"

    Save-Checkpoint 7
    Write-TreeSpacer
}

if ($startStep -le 7) {
    Write-TreeStep "7/8" "Update Compatibility & Promotion"
    Invoke-StepScript { & "$repository/Tools/Assert-UpdateCompatibility.ps1" } | Out-Null
    $projectPath = Join-Path $repository 'V-Notch.csproj'
    if (Test-Path -LiteralPath $projectPath) {
        [xml]$project = Get-Content -LiteralPath $projectPath
        $version = [string]($project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
    } else {
        $version = '1.9.4'
    }
    Invoke-StepScript { & "$repository/Tools/Assert-UpdateCompatibility.ps1" -ReleaseVersion $version -InstalledVersion "$version-beta.65534.65534+build.promotion-probe" } | Out-Null
    Invoke-StepScript { & "$repository/Tools/Assert-ForwardUpdateCompatibility.ps1" } | Out-Null
    Write-TreeItem "Verified 1.9.3 client & beta-to-stable v$version promotion." -State "Success"

    Save-Checkpoint 8
    Write-TreeSpacer
}

if ($startStep -le 8) {
    Write-TreeStep "8/8" "NuGet Package Vulnerability Audit"
    Invoke-StepScript { & "$repository/Tools/Assert-PackageAudit.ps1" -Solution "$repository/V-Notch.sln" -OutputPath "$runDirectory/package-audit.json" } | Out-Null
    Write-TreeItem "0 known vulnerable packages found." -State "Success"

    Save-Checkpoint 9
}

if (-not $env:VNOTCH_CI_RESUME_TEST_FIXTURE) {
    if (Test-Path -LiteralPath $runDirectory) {
        Remove-Item -LiteralPath $runDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ((Test-Path -LiteralPath $artifactsDir) -and @(Get-ChildItem -LiteralPath $artifactsDir -Force -ErrorAction SilentlyContinue).Count -eq 0) {
        Remove-Item -LiteralPath $artifactsDir -Force -ErrorAction SilentlyContinue
    }
    foreach ($cleanupRel in @('Tests\TestResults', 'Tests\bin', 'Tools\ShaderCompiler\bin', 'Tools\ShaderCompiler\obj', 'Uninstall\bin')) {
        $cleanupPath = Join-Path $repository $cleanupRel
        if (Test-Path -LiteralPath $cleanupPath) {
            Remove-Item -LiteralPath $cleanupPath -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

$stopwatch.Stop()
$elapsed = $stopwatch.Elapsed

Write-TreeSpacer
Write-Host "  $gBotLeft$lineRule" -ForegroundColor DarkCyan
Write-Host "     $gCheck ALL CHECKS PASSED " -ForegroundColor Green -NoNewline
Write-Host ("Completed in {0}" -f $elapsed.ToString("mm\:ss")) -ForegroundColor Gray
Write-Host ""
} finally {
    [Environment]::SetEnvironmentVariable('VNOTCH_QA_ARTIFACT_DIR', $previousQaDirectory, 'Process')
    [Environment]::SetEnvironmentVariable('VNOTCH_RUN_DESKTOP_TESTS', $previousDesktopMode, 'Process')
    [Environment]::SetEnvironmentVariable('EnableWindowsTargeting', $previousWinTarget, 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_SKIP_FIRST_TIME_EXPERIENCE', $previousSkipFirst, 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_NOLOGO', $previousNoLogo, 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_CLI_TELEMETRY_OPTOUT', $previousTelemetry, 'Process')
    [Environment]::SetEnvironmentVariable('PYTHONDONTWRITEBYTECODE', $previousPyBytecode, 'Process')
    Pop-Location
}
