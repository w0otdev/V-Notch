# Pre-push CI verification script for V-Notch
# Mirrors .github/workflows/ci.yml locally

param(
    [switch]$FixFormat,
    [switch]$IncludeDesktopIntegration
)

$ErrorActionPreference = "Stop"
Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process -Force
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$env:GIT_TERMINAL_PROMPT = "0"
$env:GCM_INTERACTIVE = "never"
$env:Path = ($env:Path -split ";" | Where-Object { $_ -ne "C:\Program Files\Git\mingw64\bin" }) -join ";"

$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition }
$repository = Split-Path $scriptDir -Parent

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

$totalSw = [System.Diagnostics.Stopwatch]::StartNew()
$modeText = if ($IncludeDesktopIntegration) { "Full + Desktop UI" } else { "Headless CI" }

Write-Host ""
Write-Host "  $gTopLeft$lineRule" -ForegroundColor DarkCyan
Write-Host "  $gVert  " -ForegroundColor DarkCyan -NoNewline
Write-Host "$gDiamond V-NOTCH PRE-PUSH VERIFICATION" -ForegroundColor Cyan -NoNewline
Write-Host "  $gVert  " -ForegroundColor DarkGray -NoNewline
Write-Host $modeText -ForegroundColor Green
Write-TreeSpacer

Write-TreeStep "1/5" "Restore Solution Dependencies"
$restOut = & dotnet restore "$repository/V-Notch.sln" -nologo -v:q 2>&1
if ($LASTEXITCODE -ne 0) {
    foreach ($l in @($restOut | ForEach-Object { "$_" } | Where-Object { $_.Trim() })) { Write-TreeItem $l -State "Error" }
    Write-TreeItem "Restore failed." -State "Error"
    Write-Host "  $gBotLeft$lineRule`n" -ForegroundColor DarkCyan
    exit 1
}
Write-TreeItem "Dependencies restored." -State "Success"

Write-TreeSpacer
Write-TreeStep "2/5" "Verify Code Formatting"
if ($FixFormat) {
    Write-TreeItem "Auto-formatting code first (-FixFormat)..." -State "Info"
    & dotnet format "$repository/V-Notch.sln" | Out-Null
}

$fmtOut = & dotnet format "$repository/V-Notch.sln" --verify-no-changes --no-restore 2>&1
if ($LASTEXITCODE -ne 0) {
    foreach ($l in @($fmtOut | ForEach-Object { "$_" } | Where-Object { $_.Trim() })) { Write-TreeItem $l -State "Error" }
    Write-TreeItem "Code format verification failed! Run '.\scripts\f.ps1'." -State "Warn"
    Write-Host "  $gBotLeft$lineRule`n" -ForegroundColor DarkCyan
    exit 1
}
Write-TreeItem "Formatting check passed." -State "Success"

Write-TreeSpacer
Write-TreeStep "3/5" "Build Solution (Release + WarnAsError)"
$bldSw = [System.Diagnostics.Stopwatch]::StartNew()
$bldOut = & dotnet build "$repository/V-Notch.sln" --configuration Release --no-restore -warnaserror -nologo -v:q -clp:NoSummary 2>&1
$bldSw.Stop()
if ($LASTEXITCODE -ne 0) {
    foreach ($l in @($bldOut | ForEach-Object { "$_" } | Where-Object { $_.Trim() })) { Write-TreeItem $l -State "Error" }
    Write-TreeItem "Build failed with warnings or errors." -State "Error"
    Write-Host "  $gBotLeft$lineRule`n" -ForegroundColor DarkCyan
    exit 1
}
Write-TreeItem ("Release build succeeded (0 warnings, 0 errors) in {0:N1}s." -f $bldSw.Elapsed.TotalSeconds) -State "Success"

Write-TreeSpacer
Write-TreeStep "4/5" "Run Tests & Enforce Code Coverage"
$coverageResults = Join-Path $repository ('artifacts/prepush-coverage-' + [guid]::NewGuid().ToString('N'))
try {
    $tstSw = [System.Diagnostics.Stopwatch]::StartNew()
    if (-not $IncludeDesktopIntegration) {
        Write-TreeItem "Mode: Headless (Category!=DesktopIntegration)" -State "Info"
        $tstOut = & dotnet test "$repository/Tests/VNotch.Tests.csproj" --configuration Release --no-build --no-restore --filter "Category!=DesktopIntegration" --settings "$repository/Tests/CI.runsettings" --collect "Code Coverage" --results-directory $coverageResults -nologo -v:q 2>&1
    } else {
        Write-TreeItem "Mode: Full suite including DesktopIntegration" -State "Info"
        $previousDesktopTestMode = [Environment]::GetEnvironmentVariable('VNOTCH_RUN_DESKTOP_TESTS', 'Process')
        try {
            $env:VNOTCH_RUN_DESKTOP_TESTS = '1'
            $tstOut = & dotnet test "$repository/Tests/VNotch.Tests.csproj" --configuration Release --no-build --no-restore --settings "$repository/Tests/CI.runsettings" --collect "Code Coverage" --results-directory $coverageResults -nologo -v:q 2>&1
        } finally {
            [Environment]::SetEnvironmentVariable('VNOTCH_RUN_DESKTOP_TESTS', $previousDesktopTestMode, 'Process')
        }
    }
    $tstSw.Stop()
    if ($LASTEXITCODE -ne 0) {
        foreach ($l in @($tstOut | ForEach-Object { "$_" } | Where-Object { $_.Trim() })) { Write-TreeItem $l -State "Error" }
        throw 'Test run failed.'
    }
    Write-TreeItem ("Tests passed in {0:N1}s." -f $tstSw.Elapsed.TotalSeconds) -State "Success"

    # Use the same assembly scope and threshold as c.ps1 and GitHub Actions.
    $covOut = & "$repository/Tools/Assert-Coverage.ps1" -ResultsDirectory $coverageResults 2>&1
    foreach ($l in @($covOut | ForEach-Object { "$_" } | Where-Object { $_.Trim() })) {
        Write-TreeItem $l.Trim() -State "Success"
    }
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

Write-TreeSpacer
Write-TreeStep "5/5" "Audit NuGet Packages"
$audOut = & dotnet list "$repository/V-Notch.sln" package --vulnerable --include-transitive 2>&1
if ($LASTEXITCODE -ne 0) {
    foreach ($l in @($audOut | ForEach-Object { "$_" } | Where-Object { $_.Trim() })) { Write-TreeItem $l -State "Error" }
    Write-TreeItem "Vulnerable packages detected." -State "Error"
    Write-Host "  $gBotLeft$lineRule`n" -ForegroundColor DarkCyan
    exit 1
}
Write-TreeItem "0 vulnerable packages detected." -State "Success"

$totalSw.Stop()
Write-TreeSpacer
Write-Host "  $gBotLeft$lineRule" -ForegroundColor DarkCyan
Write-Host "     $gCheck READY TO PUSH " -ForegroundColor Green -NoNewline
Write-Host ("All pre-push checks passed in {0:mm\:ss}." -f $totalSw.Elapsed) -ForegroundColor Gray
Write-Host ""
exit 0
