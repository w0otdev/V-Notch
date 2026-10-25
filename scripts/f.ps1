<#
.SYNOPSIS
    Formats C# code in the solution using the repository's .editorconfig.
.DESCRIPTION
    Uses the same dotnet format command as scripts/c.ps1. Can be called from
    any directory. Does not format XAML, JSON, or PowerShell files.
.PARAMETER Check
    Checks formatting without changing files; returns a nonzero exit code on differences.
.PARAMETER NoRestore
    Skips package restore when dependencies are already restored.
.PARAMETER Include
    Optional files or directories, relative to the repository root.
.EXAMPLE
    .\scripts\f.ps1
.EXAMPLE
    .\scripts\f.ps1 -Check -NoRestore
.EXAMPLE
    .\scripts\f.ps1 -Include 'Windows', 'Services/Translation'
#>
[CmdletBinding()]
param(
    [switch]$Check,
    [switch]$NoRestore,
    [string[]]$Include = @()
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ge 7) {
    $PSNativeCommandUseErrorActionPreference = $false
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Invoke-CapturedNative([scriptblock]$Command) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Command 2>&1
    } finally {
        $ErrorActionPreference = $prev
    }
}

$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition }
$repository = Split-Path $scriptDir -Parent
$formatExitCode = 1

$gTopLeft = [char]0x256D
$gBotLeft = [char]0x2570
$gVert    = [char]0x2502
$gBranch  = [char]0x251C
$gHoriz   = [char]0x2500
$gDiamond = [char]0x25C6
$gCheck   = [char]0x2714
$gCross   = [char]0x2716
$gArrow   = [char]0x25B8
$lineRule = ([string]$gHoriz) * 56

Push-Location -LiteralPath $repository
try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'dotnet was not found. Install the .NET SDK specified in global.json.'
    }

    $modeLabel = if ($Check) { 'Verify Only (-Check)' } else { 'Auto-Format Solution' }
    $modeColor = if ($Check) { 'Yellow' } else { 'Green' }

    Write-Host ""
    Write-Host "  $gTopLeft$lineRule" -ForegroundColor DarkCyan
    Write-Host "  $gVert  " -ForegroundColor DarkCyan -NoNewline
    Write-Host "$gDiamond V-NOTCH CODE FORMATTER" -ForegroundColor Cyan -NoNewline
    Write-Host "  $gVert  " -ForegroundColor DarkGray -NoNewline
    Write-Host $modeLabel -ForegroundColor $modeColor
    Write-Host "  $gVert" -ForegroundColor DarkCyan

    $formatArguments = @('format', 'whitespace', (Join-Path $repository 'V-Notch.sln'))
    if ($Check) { $formatArguments += '--verify-no-changes' }
    if ($NoRestore -or (Test-Path -LiteralPath (Join-Path $repository 'obj\project.assets.json'))) {
        $formatArguments += '--no-restore'
    }
    if ($Include.Count -eq 0) {
        $changed = @(Invoke-CapturedNative { & git diff --name-only HEAD -- '*.cs' '*.csproj' '*.props' '*.targets' } |
            ForEach-Object { "$_" } | Where-Object { $_.Trim() -and (Test-Path -LiteralPath (Join-Path $repository $_.Trim())) })
        $untracked = @(Invoke-CapturedNative { & git ls-files --others --exclude-standard -- '*.cs' '*.csproj' '*.props' '*.targets' } |
            ForEach-Object { "$_" } | Where-Object { $_.Trim() -and (Test-Path -LiteralPath (Join-Path $repository $_.Trim())) })
        $Include = @($changed + $untracked | Select-Object -Unique)
    }
    if ($Include.Count -gt 0) {
        $formatArguments += '--include'
        $formatArguments += $Include
    }

    Write-Host "  $gBranch$gHoriz " -ForegroundColor DarkCyan -NoNewline
    Write-Host "[1/1] " -ForegroundColor Cyan -NoNewline
    Write-Host "Running dotnet format" -ForegroundColor White

    if ($Include.Count -gt 0) {
        Write-Host "  $gVert     $gArrow " -ForegroundColor DarkCyan -NoNewline
        Write-Host ("Scope : " + ($Include -join ', ')) -ForegroundColor Gray
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $output = Invoke-CapturedNative { & dotnet @formatArguments }
    $formatExitCode = $LASTEXITCODE
    $sw.Stop()

    $lines = @($output | ForEach-Object { "$_" } | Where-Object { $_.Trim().Length -gt 0 })
    foreach ($line in $lines) {
        Write-Host "  $gVert     " -ForegroundColor DarkCyan -NoNewline
        Write-Host $line.Trim() -ForegroundColor DarkGray
    }

    if ($formatExitCode -eq 0) {
        $msg = if ($Check) { "Formatting check passed in {0:N2}s." -f $sw.Elapsed.TotalSeconds } else { "Formatting completed in {0:N2}s." -f $sw.Elapsed.TotalSeconds }
        Write-Host "  $gVert     $gCheck " -ForegroundColor Green -NoNewline
        Write-Host $msg -ForegroundColor Gray
        Write-Host "  $gVert" -ForegroundColor DarkCyan
        Write-Host "  $gBotLeft$lineRule" -ForegroundColor DarkCyan
        Write-Host "     $gCheck DONE " -ForegroundColor Green -NoNewline
        Write-Host $msg -ForegroundColor Gray
        Write-Host ""
    } else {
        Write-Host "  $gVert     $gCross " -ForegroundColor Red -NoNewline
        Write-Host ("Formatting failed (exit code $formatExitCode).") -ForegroundColor Red
        Write-Host "  $gVert" -ForegroundColor DarkCyan
        Write-Host "  $gBotLeft$lineRule`n" -ForegroundColor DarkCyan
    }
}
finally {
    Pop-Location
}

exit $formatExitCode
