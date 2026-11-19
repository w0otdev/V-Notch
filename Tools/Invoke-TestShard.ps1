[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateRange(0, 63)][int]$ShardIndex,
    [Parameter(Mandatory)][ValidateRange(1, 64)][int]$TotalShards,
    [string]$ProjectPath = 'Tests/VNotch.Tests.csproj',
    [string]$Configuration = 'Release',
    [string]$ResultsDirectory = 'TestResults/Coverage',
    [string]$BaseFilter = 'Category!=DesktopIntegration',
    [switch]$CollectCoverage,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

if ($ShardIndex -ge $TotalShards) {
    throw "ShardIndex ($ShardIndex) must be less than TotalShards ($TotalShards)."
}

$repoRoot = Split-Path $PSScriptRoot -Parent
$resolvedProject = if ([IO.Path]::IsPathRooted($ProjectPath)) { $ProjectPath } else { Join-Path $repoRoot $ProjectPath }
$projectDir = Split-Path $resolvedProject -Parent
$projectStem = [IO.Path]::GetFileNameWithoutExtension($resolvedProject)
$binRoot = Join-Path $projectDir "bin\$Configuration"

$testDll = Get-ChildItem -LiteralPath $binRoot -Filter "$projectStem.dll" -Recurse -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1

if (-not $testDll) {
    throw "Could not find built test assembly '$projectStem.dll' under '$binRoot'. Build the test project first."
}

$tempDir = Join-Path ([IO.Path]::GetTempPath()) ('vnotch-shard-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir | Out-Null

try {
    $listFile = Join-Path $tempDir 'all-tests.txt'
    & dotnet vstest $testDll.FullName /ListFullyQualifiedTests "/ListTestsTargetPath:$listFile" | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $listFile)) {
        throw "Failed to enumerate tests from '$($testDll.FullName)'."
    }

    $rawTests = @(Get-Content -LiteralPath $listFile | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($rawTests.Count -eq 0) {
        throw "No tests were discovered in '$($testDll.FullName)'."
    }

    # Group by test class so all methods/theories in a class run together on one shard.
    $classCounts = [System.Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
    foreach ($line in $rawTests) {
        $methodPart = $line.Trim() -replace '\(.*$', ''
        $dotIndex = $methodPart.LastIndexOf('.')
        $className = if ($dotIndex -gt 0) { $methodPart.Substring(0, $dotIndex) } else { $methodPart }
        if ($classCounts.ContainsKey($className)) {
            $classCounts[$className]++
        } else {
            $classCounts[$className] = 1
        }
    }

    # Runners discover tests independently. A difference in discovered row counts
    # must never move a class onto a different shard.
    $selectedClasses = [System.Collections.Generic.List[string]]::new()
    $selectedTestCount = 0
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        foreach ($className in ($classCounts.Keys | Sort-Object)) {
            $hash = $sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($className))
            $owner = [BitConverter]::ToUInt32($hash, 0) % $TotalShards
            if ($owner -eq $ShardIndex) {
                $selectedClasses.Add($className)
                $selectedTestCount += $classCounts[$className]
            }
        }
    } finally {
        $sha256.Dispose()
    }

    Write-Host ("Shard {0}/{1}: {2} test classes, ~{3} test methods (of {4} total)." -f `
        ($ShardIndex + 1), $TotalShards, $selectedClasses.Count, $selectedTestCount, $rawTests.Count)

    if ($selectedClasses.Count -eq 0) {
        Write-Host 'No test classes assigned to this shard.'
        exit 0
    }

    $classClauses = @($selectedClasses | ForEach-Object { "FullyQualifiedName~$_." })
    $shardClause = '(' + ($classClauses -join '|') + ')'
    $fullFilter = if ([string]::IsNullOrWhiteSpace($BaseFilter)) {
        $shardClause
    } else {
        "($BaseFilter)&$shardClause"
    }

    if ($DryRun) {
        Write-Host "Filter length: $($fullFilter.Length) chars"
        exit 0
    }

    # Keep large filters out of the command line without sending quoted CLI arguments through MSBuild response-file parsing.
    $resolvedResults = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($ResultsDirectory)) { $ResultsDirectory } else { Join-Path $repoRoot $ResultsDirectory }))
    New-Item -ItemType Directory -Path $resolvedResults -Force | Out-Null
    $settingsFile = Join-Path $resolvedResults "shard-$ShardIndex.runsettings"
    [xml]$settings = '<RunSettings><RunConfiguration><TestCaseFilter /><EnvironmentVariables><VNOTCH_TEST_DIAGNOSTICS_DIRECTORY /></EnvironmentVariables></RunConfiguration></RunSettings>'
    $settings.RunSettings.RunConfiguration.TestCaseFilter = $fullFilter
    $settings.RunSettings.RunConfiguration.EnvironmentVariables.VNOTCH_TEST_DIAGNOSTICS_DIRECTORY = $resolvedResults
    if ($CollectCoverage) {
        # Coverlet cannot resolve WPF metadata in V-Notch.dll and silently drops it; use the Microsoft collector config.
        [xml]$ci = Get-Content -LiteralPath (Join-Path $repoRoot 'Tests/CI.runsettings') -Raw
        $settings.RunSettings.AppendChild($settings.ImportNode($ci.RunSettings.DataCollectionRunSettings, $true)) | Out-Null
    }
    $settings.Save($settingsFile)
    $argsList = [System.Collections.Generic.List[string]]::new()
    $argsList.Add('--configuration')
    $argsList.Add($Configuration)
    $argsList.Add('--no-build')
    $argsList.Add('--no-restore')
    $argsList.Add('--settings')
    $argsList.Add($settingsFile)
    $argsList.Add('--blame-hang-timeout')
    $argsList.Add('60s')
    $argsList.Add('--blame-crash-dump-type')
    $argsList.Add('mini')
    $argsList.Add('--diag')
    $argsList.Add((Join-Path $resolvedResults "shard-$ShardIndex.vstest.log"))
    $argsList.Add('--results-directory')
    $argsList.Add($resolvedResults)
    $argsList.Add('--logger')
    $argsList.Add("trx;LogFileName=shard-$ShardIndex.trx")
    $argsList.Add('--logger')
    $argsList.Add('console;verbosity=minimal')
    if ($CollectCoverage) {
        $argsList.Add('--collect')
        $argsList.Add('Code Coverage')
    }

    & dotnet test $resolvedProject @argsList
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
} finally {
    if (Test-Path -LiteralPath $tempDir) {
        Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
