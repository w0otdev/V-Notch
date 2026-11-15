param(
    [Parameter(Mandatory = $true)][string]$ObjDir,
    [Parameter(Mandatory = $true)][string]$FlagsSignature,
    [string]$VcVars64Path = '',
    [string]$RequestedJobs = ''
)

$ErrorActionPreference = 'Stop'
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)

function Read-RspPaths([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return @() }
    $items = [System.Collections.Generic.List[string]]::new()
    foreach ($raw in [System.IO.File]::ReadAllLines($path)) {
        $trimmed = $raw.Trim().Trim('"')
        if ($trimmed.Length -gt 0) {
            $items.Add($trimmed)
        }
    }
    return $items
}

function Write-RspFile([string]$path, [System.Collections.Generic.List[string]]$lines) {
    if ($lines.Count -eq 0) {
        [System.IO.File]::WriteAllBytes($path, [byte[]]@())
        return
    }
    [System.IO.File]::WriteAllLines($path, $lines, $utf8NoBom)
}

$flagsFile = Join-Path $ObjDir 'compile.flags'
$forceRebuild = $true
if (Test-Path -LiteralPath $flagsFile) {
    $previousFlags = [System.IO.File]::ReadAllText($flagsFile, $utf8NoBom).Trim()
    if ($previousFlags -eq $FlagsSignature) {
        $forceRebuild = $false
    }
}

$buckets = @(
    @{ Name = 'hot';  AllRsp = 'all-hot.rsp';  OutRsp = 'baseline-hot.rsp' },
    @{ Name = 'cold'; AllRsp = 'all-cold.rsp'; OutRsp = 'baseline-cold.rsp' },
    @{ Name = 'avx2'; AllRsp = 'all-avx2.rsp'; OutRsp = 'avx2.rsp' }
)

if ($forceRebuild) {
    foreach ($bucket in $buckets) {
        $bucketDir = Join-Path $ObjDir $bucket.Name
        if (Test-Path -LiteralPath $bucketDir) {
            Get-ChildItem -LiteralPath $bucketDir -Filter '*.obj' -File -ErrorAction SilentlyContinue | Remove-Item -Force
        }
    }
    [System.IO.File]::WriteAllText($flagsFile, $FlagsSignature, $utf8NoBom)
}

[int64]$maxDepTicks = 0
foreach ($dep in (Read-RspPaths (Join-Path $ObjDir 'all-deps.rsp'))) {
    if ([System.IO.File]::Exists($dep)) {
        $ticks = [System.IO.File]::GetLastWriteTimeUtc($dep).Ticks
        if ($ticks -gt $maxDepTicks) {
            $maxDepTicks = $ticks
        }
    }
}

$linkLines = [System.Collections.Generic.List[string]]::new()

foreach ($bucket in $buckets) {
    $bucketDir = Join-Path $ObjDir $bucket.Name
    $expectedNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $compileLines = [System.Collections.Generic.List[string]]::new()

    foreach ($src in (Read-RspPaths (Join-Path $ObjDir $bucket.AllRsp))) {
        $objName = [System.IO.Path]::GetFileNameWithoutExtension($src) + '.obj'
        $objPath = Join-Path $bucketDir $objName
        $null = $expectedNames.Add($objName)
        $linkLines.Add('"' + $objPath + '"')

        $needsCompile = $forceRebuild -or -not [System.IO.File]::Exists($objPath)
        if (-not $needsCompile) {
            $objTicks = [System.IO.File]::GetLastWriteTimeUtc($objPath).Ticks
            $srcTicks = [System.IO.File]::GetLastWriteTimeUtc($src).Ticks
            if ($srcTicks -gt $objTicks -or $maxDepTicks -gt $objTicks) {
                $needsCompile = $true
            }
        }
        if ($needsCompile) {
            $compileLines.Add('"' + $src + '"')
        }
    }

    if (Test-Path -LiteralPath $bucketDir) {
        foreach ($existing in [System.IO.Directory]::EnumerateFiles($bucketDir, '*.obj')) {
            if (-not $expectedNames.Contains([System.IO.Path]::GetFileName($existing))) {
                [System.IO.File]::Delete($existing)
            }
        }
    }

    Write-RspFile (Join-Path $ObjDir $bucket.OutRsp) $compileLines
}

Write-RspFile (Join-Path $ObjDir 'link.rsp') $linkLines

$isCi = ($env:GITHUB_ACTIONS -eq 'true' -or $env:CI -eq 'true')
$envCmdPath = Join-Path $ObjDir 'vcvars64.env.cmd'
$envMetaPath = Join-Path $ObjDir 'vcvars64.env.meta'
if (-not [string]::IsNullOrWhiteSpace($VcVars64Path) -and [System.IO.File]::Exists($VcVars64Path)) {
    $expectedMeta = "$(if ($isCi) { 'ci' } else { 'local' })|$VcVars64Path|$([System.IO.File]::GetLastWriteTimeUtc($VcVars64Path).Ticks)"
    $currentMeta = if ([System.IO.File]::Exists($envMetaPath)) { [System.IO.File]::ReadAllText($envMetaPath, $utf8NoBom).Trim() } else { '' }
    if ($currentMeta -ne $expectedMeta -or -not [System.IO.File]::Exists($envCmdPath)) {
        $writtenEnv = $false
        if (-not $isCi) {
            $captured = & cmd.exe /d /s /c "call `"$VcVars64Path`" >nul && set PATH && set INCLUDE && set LIB && set LIBPATH && set EXTERNAL_INCLUDE" 2>$null
            $envLines = [System.Collections.Generic.List[string]]::new()
            $envLines.Add('@echo off')
            foreach ($entry in $captured) {
                if ($entry -match '^(PATH|INCLUDE|LIB|LIBPATH|EXTERNAL_INCLUDE)=(.*)$') {
                    $cleanVal = $Matches[2].Replace('"', '')
                    $envLines.Add("set `"$($Matches[1])=$cleanVal`"")
                }
            }
            if ($envLines.Count -ge 4) {
                [System.IO.File]::WriteAllLines($envCmdPath, $envLines, $utf8NoBom)
                $writtenEnv = $true
            }
        }
        if (-not $writtenEnv) {
            [System.IO.File]::WriteAllText($envCmdPath, "@echo off`r`ncall `"$VcVars64Path`" >nul`r`n", $utf8NoBom)
        }
        [System.IO.File]::WriteAllText($envMetaPath, $expectedMeta, $utf8NoBom)
    }
}

$parsedJobs = 0
if (-not [string]::IsNullOrWhiteSpace($RequestedJobs) -and [int]::TryParse($RequestedJobs, [ref]$parsedJobs) -and $parsedJobs -gt 0) {
    Write-Output "/MP$parsedJobs"
} elseif ($isCi) {
    Write-Output '/MP'
} else {
    $jobs = [Math]::Min(6, [Math]::Max(1, [int][Math]::Floor([Environment]::ProcessorCount / 2)))
    Write-Output "/MP$jobs"
}
