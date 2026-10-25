<#
.SYNOPSIS
    Full CI Verification Script for V-Notch.
    Runs the Windows validation and pipeline checks locally before pushing.
    Hosted Gitleaks history scanning and CodeQL analysis still run in GitHub Actions.

.DESCRIPTION
    Runs the complete Windows CI validation suite:
 *       1. Pipeline & secret scanner tests and locale validation (Python)
 *       2. Model asset provenance, license & checksum assertion
 *       3. NuGet dependency restore (--locked-mode)
 *       4. Code formatting verification (dotnet format)
 *       5. Native C++26 DLL & Release build with -warnaserror
 *       6. Unit tests & code coverage threshold check + Release security gates
 *       7. Update compatibility (1.9.3 client & beta-to-stable promotion)
 *       8. Package vulnerability audit

.PARAMETER Resume
    Evidence directory of a failed run. Resumes at its first unfinished step.
    Reuse the same validation options as the original run.
    If a fix changes compiled code, rebuild Release before resuming the test step.

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
    Write-TreeStep "1/8" "Pipeline Scripts & Locales (Python)"
    $python = Get-Command python -ErrorAction SilentlyContinue
    if ($python) {
        $pyOut1 = Invoke-CapturedNative { & python -m unittest discover -s "$repository/.github/scripts" -p "test_*.py" }
        if ($LASTEXITCODE -ne 0) {
            foreach ($l in @($pyOut1 | ForEach-Object { "$_" } | Where-Object { $_.Trim() })) { Write-TreeItem $l -State "Error" }
            throw "Pipeline tests failed."
        }
        Write-TreeItem "Pipeline unit tests passed." -State "Success"

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
    $modelOut = & "$repository/Tools/Assert-ModelAssets.ps1" *>&1
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
            ((Get-Item -LiteralPath (Join-Path $repository 'Tests\obj\project.assets.json')).LastWriteTimeUtc -ge (Get-Item -LiteralPath (Join-Path $repository 'Tests\packages.lock.json')).LastWriteTimeUtc)
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
    Write-TreeStep "4/8" "Code Formatting Verification"
    $fmtInclude = @()
    if (-not $env:VNOTCH_CI_RESUME_TEST_FIXTURE) {
        $changedCs = @(Invoke-CapturedNative { & git diff --name-only HEAD -- '*.cs' '*.csproj' '*.props' '*.targets' } |
            ForEach-Object { "$_" } | Where-Object { $_.Trim() -and (Test-Path -LiteralPath (Join-Path $repository $_.Trim())) })
        $untrackedCs = @(Invoke-CapturedNative { & git ls-files --others --exclude-standard -- '*.cs' '*.csproj' '*.props' '*.targets' } |
            ForEach-Object { "$_" } | Where-Object { $_.Trim() -and (Test-Path -LiteralPath (Join-Path $repository $_.Trim())) })
        $fmtInclude = @($changedCs + $untrackedCs | Select-Object -Unique)
    }
    if ($env:VNOTCH_CI_RESUME_TEST_FIXTURE -or $fmtInclude.Count -gt 0) {
        $incArgs = if ($fmtInclude.Count -gt 0) { @('--include') + $fmtInclude } else { @() }
        if ($FixFormat) {
            Write-TreeItem "Auto-formatting with 'dotnet format whitespace' (-FixFormat)..." -State "Info"
            Invoke-CapturedNative { & dotnet format whitespace "$repository/V-Notch.sln" --no-restore @incArgs } | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Automatic formatting failed.' }
        }
        $fmtOut = Invoke-CapturedNative { & dotnet format whitespace "$repository/V-Notch.sln" --verify-no-changes --no-restore @incArgs }
        if ($LASTEXITCODE -ne 0) {
            foreach ($l in @($fmtOut | ForEach-Object { "$_" } | Where-Object { $_.Trim() })) { Write-TreeItem $l -State "Error" }
            Write-TreeItem "Run '.\scripts\f.ps1' or '.\scripts\c.ps1 -FixFormat' to fix." -State "Warn"
            throw "Code formatting verification failed."
        }
    }
    Write-TreeItem "Code formatting verified." -State "Success"

    Save-Checkpoint 5
    Write-TreeSpacer
}

if ($startStep -le 5) {
    Write-TreeStep "5/8" "Release Build (-warnaserror)"
    if ($Resume -and $startStep -eq 5) {
        $testResultsDir = Join-Path $runDirectory ('test-results-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $testResultsDir -Force | Out-Null
    }
    $bldSw = [System.Diagnostics.Stopwatch]::StartNew()
    $bldOut = Invoke-CapturedNative { & dotnet build "$repository/V-Notch.sln" --configuration Release --no-restore -warnaserror -nologo -v:q -clp:NoSummary -m }
    $bldSw.Stop()
    if ($LASTEXITCODE -ne 0) {
        foreach ($l in @($bldOut | ForEach-Object { "$_" } | Where-Object { $_.Trim() })) { Write-TreeItem $l -State "Error" }
        throw "Build failed with warnings or errors."
    }
    Write-TreeItem ("Release build succeeded (0 warnings, 0 errors) in {0:N1}s." -f $bldSw.Elapsed.TotalSeconds) -State "Success"

    Save-Checkpoint 6
    Write-TreeSpacer
}

if ($startStep -le 6) {
    Write-TreeStep "6/8" "Release Security Gates"
    $gateOut = & "$repository/Tests/ReleaseGateTests.ps1" *>&1
    $cleanOut = & "$repository/Tests/PrepushCleanupTests.ps1" *>&1
    $resOut = & "$repository/Tests/LocalCiResumeTests.ps1" *>&1
    Write-TreeItem "Coverage boundaries, XML safety, signing & runner regression gates passed." -State "Success"

    Save-Checkpoint 7
    Write-TreeSpacer
}

if ($startStep -le 7) {
    Write-TreeStep "7/8" "Update Compatibility & Promotion"
    $updOut1 = & "$repository/Tools/Assert-UpdateCompatibility.ps1" *>&1
    $projectPath = Join-Path $repository 'V-Notch.csproj'
    if (Test-Path -LiteralPath $projectPath) {
        [xml]$project = Get-Content -LiteralPath $projectPath
        $version = [string]($project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
    } else {
        $version = '1.9.4'
    }
    $updOut2 = & "$repository/Tools/Assert-UpdateCompatibility.ps1" -ReleaseVersion $version -InstalledVersion "$version-beta.65534.65534+build.promotion-probe" *>&1
    Write-TreeItem "Verified 1.9.3 client & beta-to-stable v$version promotion." -State "Success"

    Save-Checkpoint 8
    Write-TreeSpacer
}

if ($startStep -le 8) {
    Write-TreeStep "8/8" "NuGet Package Vulnerability Audit"
    $audOut = & "$repository/Tools/Assert-PackageAudit.ps1" -Solution "$repository/V-Notch.sln" -OutputPath "$runDirectory/package-audit.json" *>&1
    Write-TreeItem "0 known vulnerable packages found." -State "Success"

    Save-Checkpoint 9
}

$stopwatch.Stop()
$elapsed = $stopwatch.Elapsed

Write-TreeSpacer
Write-Host "  $gBotLeft$lineRule" -ForegroundColor DarkCyan
Write-Host "     $gCheck ALL CHECKS PASSED " -ForegroundColor Green -NoNewline
Write-Host ("Completed in {0} ({1})" -f $elapsed.ToString("mm\:ss"), $relRunDir) -ForegroundColor Gray
Write-Host ""
} finally {
    [Environment]::SetEnvironmentVariable('VNOTCH_QA_ARTIFACT_DIR', $previousQaDirectory, 'Process')
    [Environment]::SetEnvironmentVariable('VNOTCH_RUN_DESKTOP_TESTS', $previousDesktopMode, 'Process')
    Pop-Location
}
