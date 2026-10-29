<#
.SYNOPSIS
    Portable repository cleanup script for V-Notch.

.DESCRIPTION
    Removes build outputs (bin, obj, release, installers), temporary test artifacts,
    benchmark results, Python caches, and leftover temporary files inside the repository.
    Can be invoked from any working directory on any machine (PowerShell 5.1+ or PowerShell 7+).
    Never deletes git-tracked files or developer workspace directories.

.PARAMETER Deep
    Also cleans local tool/IDE caches (.artifacts, .cache, .sentry-native, .nuget/packages, .vs).

.PARAMETER DryRun
    Lists items that would be removed without deleting them.

.EXAMPLE
    .\scripts\d.ps1
.EXAMPLE
    .\scripts\d.ps1 -DryRun
.EXAMPLE
    .\scripts\d.ps1 -Deep
#>
[CmdletBinding()]
param(
    [switch]$Deep,
    [switch]$DryRun
)

Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process -Force
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

# Resolve repository root portably regardless of caller working directory or symlinked script invocation
$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
}
$candidateRoot = Split-Path -Parent $scriptDir
if (-not (Test-Path -LiteralPath (Join-Path $candidateRoot 'V-Notch.sln')) -and
    (Test-Path -LiteralPath (Join-Path $scriptDir 'V-Notch.sln'))) {
    $candidateRoot = $scriptDir
}
$resolvedRepoRoot = [IO.Path]::GetFullPath($candidateRoot)

if (-not (Test-Path -LiteralPath (Join-Path $resolvedRepoRoot 'V-Notch.csproj'))) {
    throw "Could not locate V-Notch repository root from '$scriptDir'."
}

# Runtime Unicode Box-Drawing & Status Glyphs (7-bit ASCII source safe for Windows PowerShell 5.1 & pwsh 7+)
$gTopLeft = [char]0x256D # ╭
$gBotLeft = [char]0x2570 # ╰
$gVert    = [char]0x2502 # │
$gBranch  = [char]0x251C # ├
$gHoriz   = [char]0x2500 # ─
$gDiamond = [char]0x25C6 # ◆
$gCheck   = [char]0x2714 # ✔
$gWarn    = [char]0x25B2 # ▲
$gArrow   = [char]0x25B8 # ▸
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
        "Dry"     { $sym = $gWarn;  $sCol = "Yellow";   $tCol = "DarkYellow" }
    }
    Write-Host "  $gVert     " -ForegroundColor DarkCyan -NoNewline
    Write-Host "$sym " -ForegroundColor $sCol -NoNewline
    Write-Host $Message -ForegroundColor $tCol
}

Push-Location -LiteralPath $resolvedRepoRoot
try {
    $modeLabel = if ($DryRun) { "Dry-Run Preview" } elseif ($Deep) { "Deep Cleanup (-Deep)" } else { "Standard Cleanup" }
    $modeColor = if ($DryRun) { "Yellow" } elseif ($Deep) { "Magenta" } else { "Green" }

    Write-Host ""
    Write-Host "  $gTopLeft$lineRule" -ForegroundColor DarkCyan
    Write-Host "  $gVert  " -ForegroundColor DarkCyan -NoNewline
    Write-Host "$gDiamond V-NOTCH REPO CLEANER" -ForegroundColor Cyan -NoNewline
    Write-Host "  $gVert  " -ForegroundColor DarkGray -NoNewline
    Write-Host $modeLabel -ForegroundColor $modeColor
    Write-Host "  $gVert" -ForegroundColor DarkCyan

    $removedCount = 0
    $freedBytes = [long]0

    function Get-ItemSize([string]$path) {
        if (-not (Test-Path -LiteralPath $path)) { return [long]0 }
        $item = Get-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
        if (-not $item) { return [long]0 }
        if (-not $item.PSIsContainer) { return [long]$item.Length }
        $files = Get-ChildItem -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue |
            Where-Object { -not $_.PSIsContainer }
        if (-not $files) { return [long]0 }
        $sum = ($files | Measure-Object -Property Length -Sum).Sum
        if ($null -eq $sum) { return [long]0 }
        return [long]$sum
    }

    # Protect all git-tracked files if git is available and .git exists
    $trackedPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $gitCmd = Get-Command git -ErrorAction SilentlyContinue
    if ($gitCmd -and (Test-Path -LiteralPath (Join-Path $resolvedRepoRoot '.git'))) {
        $gitTracked = & $gitCmd.Source -C $resolvedRepoRoot ls-files 2>$null
        if ($LASTEXITCODE -eq 0 -and $gitTracked) {
            foreach ($rel in $gitTracked) {
                if (-not [string]::IsNullOrWhiteSpace($rel)) {
                    $fullTracked = [IO.Path]::GetFullPath((Join-Path $resolvedRepoRoot $rel))
                    [void]$trackedPaths.Add($fullTracked)
                }
            }
        }
    }

    function Test-ContainsTrackedFile([string]$fullPath) {
        if ($script:trackedPaths.Contains($fullPath)) { return $true }
        $prefix = $fullPath + [IO.Path]::DirectorySeparatorChar
        foreach ($tracked in $script:trackedPaths) {
            if ($tracked.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }
        return $false
    }

    $cleanedPrefixes = New-Object 'System.Collections.Generic.List[string]'

    function Test-IsUnderCleanedPath([string]$fullPath) {
        foreach ($prefix in $script:cleanedPrefixes) {
            if ($fullPath.Equals($prefix, [StringComparison]::OrdinalIgnoreCase) -or
                $fullPath.StartsWith($prefix + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }
        return $false
    }

    function Remove-SafeTarget([string]$targetPath) {
        if (-not (Test-Path -LiteralPath $targetPath)) { return }
        $fullPath = [IO.Path]::GetFullPath($targetPath)
        if (-not $fullPath.StartsWith($resolvedRepoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to delete path outside repository root: $fullPath"
        }
        if (Test-IsUnderCleanedPath $fullPath) { return }
        if (Test-ContainsTrackedFile $fullPath) { return }

        $size = Get-ItemSize $fullPath
        $relative = $fullPath.Substring($resolvedRepoRoot.Length + 1)
        $sizeLabel = "{0:N2} MB" -f ($size / 1MB)

        if ($DryRun) {
            Write-TreeItem "$relative ($sizeLabel)" -State "Dry"
        } else {
            Remove-Item -LiteralPath $fullPath -Recurse -Force -ErrorAction SilentlyContinue
            Write-TreeItem "$relative ($sizeLabel)" -State "Success"
        }

        $script:cleanedPrefixes.Add($fullPath)
        $script:removedCount++
        $script:freedBytes += $size
    }

    # 1. Root-level build, release, and test artifact directories
    $rootDirs = @(
        'bin',
        'bin-test',
        'bin_test',
        'obj',
        'release',
        'release_final',
        'release_secure',
        'release-debug',
        'installers',
        'artifacts',
        'TestResults',
        'BenchmarkDotNet.Artifacts',
        '.test-output',
        'build',
        'x64',
        'x86',
        'coco128-tmp',
        'attached_assets',
        'Tests\artifacts',
        'Tests\.artifacts',
        '.github\scripts\__pycache__',
        'Native\out',
        'result',
        'changes'
    )

    if ($Deep) {
        $rootDirs += @(
            '.artifacts',
            '.cache',
            '.sentry-native',
            'scripts\.sentry-native',
            '.nuget\packages',
            '.pytest_cache',
            '.vs'
        )
    }

    Write-TreeStep "1/3" "Build & Release Artifacts"
    $beforeStep1 = $removedCount
    foreach ($relDir in $rootDirs) {
        $candidate = Join-Path $resolvedRepoRoot $relDir
        Remove-SafeTarget $candidate
    }
    if ($removedCount -eq $beforeStep1) {
        Write-TreeItem "Clean (nothing to remove)." -State "Info"
    }

    # 2. Subproject bin/obj/TestResults/pycache directories (excluding source & workspace dirs)
    Write-Host "  $gVert" -ForegroundColor DarkCyan
    Write-TreeStep "2/3" "Subproject Caches (Tests, Tools, Uninstall)"
    $beforeStep2 = $removedCount
    $excludedTopDirs = @(
        '.git',
        '.github',
        '.agents',
        '.codegraph',
        '.vscode',
        '.idea',
        'awesome-codex-skills',
        'Assets',
        'Contracts',
        'Controllers',
        'Controls',
        'docs',
        'Fonts',
        'Introduction',
        'Locales',
        'Models',
        'Modules',
        'Native',
        'Presenters',
        'Security',
        'Services',
        'Setup',
        'Shaders',
        'ViewModels',
        'Windows',
        'scripts'
    )
    $targetDirNames = @('bin', 'bin-test', 'bin_test', 'obj', 'TestResults', 'BenchmarkDotNet.Artifacts', '__pycache__', '.pytest_cache')

    function Search-NestedArtifacts([string]$dirPath) {
        if (Test-IsUnderCleanedPath $dirPath) { return }
        $children = Get-ChildItem -LiteralPath $dirPath -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.PSIsContainer }
        foreach ($child in $children) {
            if (Test-IsUnderCleanedPath $child.FullName) { continue }
            if ($targetDirNames -contains $child.Name) {
                Remove-SafeTarget $child.FullName
            } else {
                Search-NestedArtifacts $child.FullName
            }
        }
    }

    $subDirs = Get-ChildItem -LiteralPath $resolvedRepoRoot -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.PSIsContainer -and ($excludedTopDirs -notcontains $_.Name) -and -not (Test-IsUnderCleanedPath $_.FullName) }

    foreach ($topDir in $subDirs) {
        Search-NestedArtifacts $topDir.FullName
    }

    # Benchmark Results inside Tools/*Benchmark
    $benchDirs = Get-ChildItem -Path (Join-Path $resolvedRepoRoot 'Tools\*Benchmark') -ErrorAction SilentlyContinue |
        Where-Object { $_.PSIsContainer }
    foreach ($bDir in $benchDirs) {
        Remove-SafeTarget (Join-Path $bDir.FullName 'Results')
        Remove-SafeTarget (Join-Path $bDir.FullName 'results.json')
    }

    if ($removedCount -eq $beforeStep2) {
        Write-TreeItem "Clean (nothing to remove)." -State "Info"
    }

    # 3. Root temporary/log files and WPF interrupted build leftovers
    Write-Host "  $gVert" -ForegroundColor DarkCyan
    Write-TreeStep "3/3" "Temporary Files & Logs"
    $beforeStep3 = $removedCount
    $rootFilePatterns = @(
        '*_wpftmp.csproj',
        '*.tmp',
        '*.log',
        '*.user',
        '*.suo',
        '*.userosscache',
        '*.sln.docstates',
        '*.v2',
        '*.vsidx',
        'debug_log.txt',
        'build_output.txt',
        'build_output_clean.txt',
        'build_output_clean_final.txt',
        'orig_cal.txt',
        'out.xml',
        'results.json',
        'Thumbs.db',
        '.DS_Store'
    )

    foreach ($pattern in $rootFilePatterns) {
        $matchedFiles = Get-ChildItem -LiteralPath $resolvedRepoRoot -Filter $pattern -Force -ErrorAction SilentlyContinue |
            Where-Object { -not $_.PSIsContainer }
        foreach ($file in $matchedFiles) {
            Remove-SafeTarget $file.FullName
        }
    }

    if ($removedCount -eq $beforeStep3) {
        Write-TreeItem "Clean (nothing to remove)." -State "Info"
    }

    $stopwatch.Stop()
    $freedMb = $freedBytes / 1MB

    Write-Host "  $gVert" -ForegroundColor DarkCyan
    Write-Host "  $gBotLeft$lineRule" -ForegroundColor DarkCyan
    Write-Host "     " -NoNewline
    if ($DryRun) {
        Write-Host "$gWarn DRY-RUN " -ForegroundColor Yellow -NoNewline
        Write-Host ("{0} item(s) found ({1:N2} MB reclaimable) in {2:N1}s." -f $removedCount, $freedMb, $stopwatch.Elapsed.TotalSeconds) -ForegroundColor Gray
    } else {
        Write-Host "$gCheck DONE " -ForegroundColor Green -NoNewline
        Write-Host ("{0} item(s) removed ({1:N2} MB freed) in {2:N1}s." -f $removedCount, $freedMb, $stopwatch.Elapsed.TotalSeconds) -ForegroundColor Gray
    }
    Write-Host ""
} finally {
    Pop-Location
}
