param(
    [string]$Filter = "",
    [switch]$All,
    [switch]$NoBuild,
    [int]$Shards = 6,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [string]$ResultsDirectory = "",
    [int]$BlameHangTimeoutSeconds = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

try {
    $testProject = Join-Path $repoRoot "Tests\VNotch.Tests.csproj"
    $nativeDll = Join-Path $repoRoot "bin\$Configuration\net8.0-windows10.0.19041.0\win-x64\VNotch.Native.dll"

    $changedFiles = @(
        git diff --name-only HEAD 2>$null
        git diff --name-only --cached 2>$null
        git ls-files --others --exclude-standard 2>$null
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique

    $nativeChanged = $false
    foreach ($f in $changedFiles) {
        if ($f -match '^Native[/\\]') {
            $nativeChanged = $true
            break
        }
    }

    if (-not $NoBuild) {
        $swBuild = [System.Diagnostics.Stopwatch]::StartNew()
        $buildArgs = @(
            "build", $testProject,
            "-c", $Configuration,
            "--no-restore",
            "-v", "minimal",
            "--nologo",
            "-p:RunAnalyzers=false",
            "-p:EnforceCodeStyleInBuild=false"
        )

        if ((Test-Path $nativeDll) -and (-not $nativeChanged)) {
            $buildArgs += "-p:SkipNativeCompile=true"
            $buildArgs += "-p:SkipShaderCompile=true"
        }

        Write-Host "[t] Incremental build..." -ForegroundColor Cyan
        & dotnet @buildArgs
        if ($LASTEXITCODE -ne 0) {
            throw "Build failed with exit code $LASTEXITCODE"
        }
        Write-Host ("[t] Build finished in {0:N1}s" -f $swBuild.Elapsed.TotalSeconds) -ForegroundColor DarkGray
    }

    $baseFilter = "Category!=DesktopIntegration"

    if (-not $All -and [string]::IsNullOrWhiteSpace($Filter)) {
        $targetClasses = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

        foreach ($file in $changedFiles) {
            $norm = $file -replace '/', '\'
            if ($norm -match '^Tests\\(.+Tests)\.cs$') {
                [void]$targetClasses.Add([System.IO.Path]::GetFileNameWithoutExtension($Matches[1]))
            }
            elseif ($norm -match '\.(cs|cpp|h)$') {
                $stem = [System.IO.Path]::GetFileNameWithoutExtension($norm) -replace '^vn_', ''
                $matchingTests = Get-ChildItem -Path (Join-Path $repoRoot "Tests") -Filter "*$stem*Tests.cs" -File -ErrorAction SilentlyContinue
                foreach ($mt in $matchingTests) {
                    [void]$targetClasses.Add($mt.BaseName)
                }
            }
        }

        if ($targetClasses.Count -gt 0) {
            $classClauses = ($targetClasses | ForEach-Object { "FullyQualifiedName~$_" }) -join "|"
            $Filter = "($classClauses)&$baseFilter"
            Write-Host ("[t] Smart diff mode -> {0} test class(es): {1}" -f $targetClasses.Count, ($targetClasses -join ", ")) -ForegroundColor Green
        }
        else {
            Write-Host "[t] No specific changed test classes detected; switching to parallel -All mode." -ForegroundColor Yellow
            $All = $true
        }
    }
    elseif (-not [string]::IsNullOrWhiteSpace($Filter) -and ($Filter -notmatch 'Category')) {
        $Filter = "($Filter)&$baseFilter"
    }

    $swTest = [System.Diagnostics.Stopwatch]::StartNew()

    if (-not $All) {
        $testArgs = @("test", $testProject, "-c", $Configuration, "--no-build", "--no-restore", "--nologo", "--logger", "console;verbosity=minimal", "--filter", $Filter)
        if ($BlameHangTimeoutSeconds -gt 0) {
            $testArgs += @("--blame-hang-timeout", "${BlameHangTimeoutSeconds}s")
        }
        if (-not [string]::IsNullOrWhiteSpace($ResultsDirectory)) {
            $testArgs += @("--results-directory", $ResultsDirectory, "--logger", "trx;LogFileName=unit-tests.trx")
        }
        & dotnet @testArgs
        if ($LASTEXITCODE -ne 0) {
            throw "Unit tests failed with exit code $LASTEXITCODE"
        }
        Write-Host ("[t] Completed in {0:N1}s" -f $swTest.Elapsed.TotalSeconds) -ForegroundColor Green
        exit 0
    }

    # Sharded parallel full-suite execution across isolated processes
    if ($Shards -eq 4) {
        $shardDefinitions = @(
            @{
                Name   = "Shard-1 (MainWindow & Greeting)"
                Filter = "$baseFilter&(FullyQualifiedName~MainWindow|FullyQualifiedName~Greeting)"
            },
            @{
                Name   = "Shard-2 (Settings, Clipboard & Spotlight)"
                Filter = "$baseFilter&(FullyQualifiedName~Settings|FullyQualifiedName~Clipboard|FullyQualifiedName~Spotlight)&FullyQualifiedName!~MainWindow&FullyQualifiedName!~Greeting"
            },
            @{
                Name   = "Shard-3 (Notch, Media, Tray, Battery & Calendar)"
                Filter = "$baseFilter&(FullyQualifiedName~Notch|FullyQualifiedName~Media|FullyQualifiedName~Tray|FullyQualifiedName~Battery|FullyQualifiedName~Calendar|FullyQualifiedName~Snip|FullyQualifiedName~SpeedTest)&FullyQualifiedName!~MainWindow&FullyQualifiedName!~Greeting&FullyQualifiedName!~Settings&FullyQualifiedName!~Clipboard&FullyQualifiedName!~Spotlight"
            },
            @{
                Name   = "Shard-4 (Remaining Core, Services & Controls)"
                Filter = "$baseFilter&FullyQualifiedName!~MainWindow&FullyQualifiedName!~Greeting&FullyQualifiedName!~Settings&FullyQualifiedName!~Clipboard&FullyQualifiedName!~Spotlight&FullyQualifiedName!~Notch&FullyQualifiedName!~Media&FullyQualifiedName!~Tray&FullyQualifiedName!~Battery&FullyQualifiedName!~Calendar&FullyQualifiedName!~Snip&FullyQualifiedName!~SpeedTest"
            }
        )
    }
    else {
        $shardDefinitions = @(
            @{
                Name   = "Shard-1 (MainWindow & Greeting)"
                Filter = "$baseFilter&(FullyQualifiedName~MainWindow|FullyQualifiedName~Greeting)"
            },
            @{
                Name   = "Shard-2 (Settings & Clipboard)"
                Filter = "$baseFilter&(FullyQualifiedName~Settings|FullyQualifiedName~Clipboard)&FullyQualifiedName!~MainWindow&FullyQualifiedName!~Greeting"
            },
            @{
                Name   = "Shard-3 (Notch & Media)"
                Filter = "$baseFilter&(FullyQualifiedName~Notch|FullyQualifiedName~Media)&FullyQualifiedName!~MainWindow&FullyQualifiedName!~Greeting&FullyQualifiedName!~Settings&FullyQualifiedName!~Clipboard"
            },
            @{
                Name   = "Shard-4 (Spotlight, Tray, Battery & Calendar)"
                Filter = "$baseFilter&(FullyQualifiedName~Spotlight|FullyQualifiedName~Tray|FullyQualifiedName~Battery|FullyQualifiedName~Calendar|FullyQualifiedName~Snip|FullyQualifiedName~SpeedTest)&FullyQualifiedName!~MainWindow&FullyQualifiedName!~Greeting&FullyQualifiedName!~Settings&FullyQualifiedName!~Clipboard&FullyQualifiedName!~Notch&FullyQualifiedName!~Media"
            },
            @{
                Name   = "Shard-5 (Pomodoro, Notes, Controls, Audio & Native)"
                Filter = "$baseFilter&(FullyQualifiedName~Pomodoro|FullyQualifiedName~Notes|FullyQualifiedName~QuickNotes|FullyQualifiedName~Control|FullyQualifiedName~Audio|FullyQualifiedName~Native|FullyQualifiedName~Todo|FullyQualifiedName~Converter|FullyQualifiedName~ColorPicker)&FullyQualifiedName!~MainWindow&FullyQualifiedName!~Greeting&FullyQualifiedName!~Settings&FullyQualifiedName!~Clipboard&FullyQualifiedName!~Notch&FullyQualifiedName!~Media&FullyQualifiedName!~Spotlight&FullyQualifiedName!~Tray&FullyQualifiedName!~Battery&FullyQualifiedName!~Calendar&FullyQualifiedName!~Snip&FullyQualifiedName!~SpeedTest"
            },
            @{
                Name   = "Shard-6 (Remaining Core & Services)"
                Filter = "$baseFilter&FullyQualifiedName!~MainWindow&FullyQualifiedName!~Greeting&FullyQualifiedName!~Settings&FullyQualifiedName!~Clipboard&FullyQualifiedName!~Notch&FullyQualifiedName!~Media&FullyQualifiedName!~Spotlight&FullyQualifiedName!~Tray&FullyQualifiedName!~Battery&FullyQualifiedName!~Calendar&FullyQualifiedName!~Snip&FullyQualifiedName!~SpeedTest&FullyQualifiedName!~Pomodoro&FullyQualifiedName!~Notes&FullyQualifiedName!~QuickNotes&FullyQualifiedName!~Control&FullyQualifiedName!~Audio&FullyQualifiedName!~Native&FullyQualifiedName!~Todo&FullyQualifiedName!~Converter&FullyQualifiedName!~ColorPicker"
            }
        )
    }

    $effectiveShards = $shardDefinitions.Count
    Write-Host ("[t] Running full unit suite across {0} parallel worker processes..." -f $effectiveShards) -ForegroundColor Cyan

    $jobs = @()
    for ($i = 0; $i -lt $effectiveShards; $i++) {
        $sd = $shardDefinitions[$i]
        $shardIdx = $i + 1
        $jobs += Start-Job -Name $sd.Name -ScriptBlock {
            param($proj, $cfg, $flt, $name, $idx, $resDir, $hangSec)
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $tArgs = @("test", $proj, "-c", $cfg, "--no-build", "--no-restore", "--nologo", "--logger", "console;verbosity=minimal", "--filter", $flt)
            if ($hangSec -gt 0) {
                $tArgs += @("--blame-hang-timeout", "${hangSec}s")
            }
            if (-not [string]::IsNullOrWhiteSpace($resDir)) {
                $tArgs += @("--results-directory", $resDir, "--logger", "trx;LogFileName=unit-tests-shard-$idx.trx")
            }
            $out = & dotnet @tArgs 2>&1
            $code = $LASTEXITCODE
            [PSCustomObject]@{
                Name     = $name
                ExitCode = $code
                Elapsed  = $sw.Elapsed.TotalSeconds
                Output   = ($out -join [Environment]::NewLine)
            }
        } -ArgumentList $testProject, $Configuration, $sd.Filter, $sd.Name, $shardIdx, $ResultsDirectory, $BlameHangTimeoutSeconds
    }

    $results = $jobs | Wait-Job | Receive-Job
    $jobs | Remove-Job -Force

    $failed = $false
    foreach ($res in ($results | Sort-Object Name)) {
        $summaryLine = ($res.Output -split "`r?`n" | Where-Object { $_ -match '(Passed!|Failed!)' } | Select-Object -Last 1)
        if ($res.ExitCode -eq 0) {
            Write-Host ("  [PASS] {0} ({1:N1}s) {2}" -f $res.Name, $res.Elapsed, $summaryLine) -ForegroundColor Green
        }
        else {
            $failed = $true
            Write-Host ("  [FAIL] {0} ({1:N1}s)" -f $res.Name, $res.Elapsed) -ForegroundColor Red
            Write-Host $res.Output
        }
    }

    if ($failed) {
        throw "One or more test shards failed."
    }

    Write-Host ("[t] Full parallel suite passed in {0:N1}s wall-clock time." -f $swTest.Elapsed.TotalSeconds) -ForegroundColor Green
}
finally {
    Pop-Location
}
