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

    # Deterministic largest-first greedy assignment keeps every shard balanced in test count.
    $sortedClasses = $classCounts.GetEnumerator() |
        Sort-Object @{ Expression = { $_.Value }; Descending = $true }, @{ Expression = { $_.Key }; Descending = $false }

    $buckets = @()
    $bucketLoads = [int[]]::new($TotalShards)
    for ($i = 0; $i -lt $TotalShards; $i++) {
        $buckets += ,([System.Collections.Generic.List[string]]::new())
    }

    foreach ($entry in $sortedClasses) {
        $minShard = 0
        for ($i = 1; $i -lt $TotalShards; $i++) {
            if ($bucketLoads[$i] -lt $bucketLoads[$minShard]) {
                $minShard = $i
            }
        }
        $buckets[$minShard].Add($entry.Key)
        $bucketLoads[$minShard] += $entry.Value
    }

    $assignedTotal = ($buckets | ForEach-Object { $_.Count } | Measure-Object -Sum).Sum
    if ($assignedTotal -ne $classCounts.Count) {
        throw "Shard partitioning mismatch: assigned $assignedTotal of $($classCounts.Count) classes."
    }

    $selectedClasses = $buckets[$ShardIndex]
    Write-Host ("Shard {0}/{1}: {2} test classes, ~{3} test methods (of {4} total)." -f `
        ($ShardIndex + 1), $TotalShards, $selectedClasses.Count, $bucketLoads[$ShardIndex], $rawTests.Count)

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

    # Write arguments to an MSBuild/dotnet response file so large filters never hit Windows command-line limits.
    $rspFile = Join-Path $tempDir "shard-$ShardIndex.rsp"
    $argsList = [System.Collections.Generic.List[string]]::new()
    $argsList.Add('--configuration')
    $argsList.Add($Configuration)
    $argsList.Add('--no-build')
    $argsList.Add('--no-restore')
    $argsList.Add('--filter')
    $argsList.Add($fullFilter)
    $argsList.Add('--blame-hang-timeout')
    $argsList.Add('60s')
    $argsList.Add('--logger')
    $argsList.Add("trx;LogFileName=shard-$ShardIndex.trx")
    $argsList.Add('--logger')
    $argsList.Add('console;verbosity=minimal')
    if ($CollectCoverage) {
        $resolvedResults = if ([IO.Path]::IsPathRooted($ResultsDirectory)) { $ResultsDirectory } else { Join-Path $repoRoot $ResultsDirectory }
        $argsList.Add('--collect:XPlat Code Coverage')
        $argsList.Add('--results-directory')
        $argsList.Add($resolvedResults)
    }

    $escapedLines = $argsList | ForEach-Object {
        if ($_ -match '[\s"&|()]') { '"' + ($_.Replace('"', '\"')) + '"' } else { $_ }
    }
    Set-Content -LiteralPath $rspFile -Value $escapedLines -Encoding UTF8

    & dotnet test $resolvedProject "@$rspFile"
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
} finally {
    if (Test-Path -LiteralPath $tempDir) {
        Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
