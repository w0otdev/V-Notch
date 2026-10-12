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
$repository = Split-Path $PSScriptRoot -Parent
$formatExitCode = 1

Push-Location -LiteralPath $repository
try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'dotnet was not found. Install the .NET SDK specified in global.json.'
    }

    $formatArguments = @('format', (Join-Path $repository 'V-Notch.sln'))
    if ($Check) { $formatArguments += '--verify-no-changes' }
    if ($NoRestore) { $formatArguments += '--no-restore' }
    if ($Include.Count -gt 0) {
        $formatArguments += '--include'
        $formatArguments += $Include
    }

    & dotnet @formatArguments
    $formatExitCode = $LASTEXITCODE
    if ($formatExitCode -eq 0) {
        if ($Check) { Write-Host 'Formatting check passed.' -ForegroundColor Green }
        else { Write-Host 'Formatting completed.' -ForegroundColor Green }
    }
}
finally {
    Pop-Location
}

exit $formatExitCode
