$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('vnotch-release-gates-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null

function Expect-Failure([scriptblock]$Action, [string]$ExpectedMessage) {
    $failed = $false
    try { & $Action } catch {
        $failed = $true
        if ($_.Exception.Message -notmatch $ExpectedMessage) { throw }
    }
    if (-not $failed) { throw "Release gate accepted invalid input; expected: $ExpectedMessage" }
}

function Write-Coverage([int]$Covered, [int]$Valid = 100, [string]$Package = 'V-Notch') {
    $xml = '<coverage lines-valid="{0}" lines-covered="{1}"><packages><package name="{2}"/></packages></coverage>' -f $Valid, $Covered, $Package
    Set-Content -LiteralPath (Join-Path $temporary 'run.cobertura.xml') -Value $xml
}

try {
    $coverageScript = Join-Path $repository 'Tools/Assert-Coverage.ps1'
    Expect-Failure { & $coverageScript -ResultsDirectory $temporary } 'No Cobertura'
    Write-Coverage 70
    & $coverageScript -ResultsDirectory $temporary
    $duplicate = Join-Path $temporary 'duplicate.cobertura.xml'
    Copy-Item -LiteralPath (Join-Path $temporary 'run.cobertura.xml') -Destination $duplicate
    & $coverageScript -ResultsDirectory $temporary
    Write-Coverage 69
    Expect-Failure { & $coverageScript -ResultsDirectory $temporary } 'different coverage reports'
    Remove-Item -LiteralPath $duplicate
    Expect-Failure { & $coverageScript -ResultsDirectory $temporary } 'below the required threshold'
    Write-Coverage 69999 100000
    Expect-Failure { & $coverageScript -ResultsDirectory $temporary } 'below the required threshold'
    Write-Coverage 70000 100000
    & $coverageScript -ResultsDirectory $temporary
    Write-Coverage 100
    & $coverageScript -ResultsDirectory $temporary
    Write-Coverage 0 0
    Expect-Failure { & $coverageScript -ResultsDirectory $temporary } 'empty or has invalid'
    Write-Coverage 100 100 'TestAssembly'
    Expect-Failure { & $coverageScript -ResultsDirectory $temporary } 'application assembly only'
    Set-Content -LiteralPath (Join-Path $temporary 'run.cobertura.xml') -Value '<!DOCTYPE coverage [<!ENTITY x SYSTEM "file:///not-read">]><coverage>&x;</coverage>'
    Expect-Failure { & $coverageScript -ResultsDirectory $temporary } 'DTD|security reasons'

    $modelRoot = Join-Path $temporary 'payload'
    $modelDirectory = Join-Path $modelRoot 'Models'
    New-Item -ItemType Directory -Path $modelDirectory | Out-Null
    foreach ($asset in 'yolox_nano.onnx', 'YOLOX-LICENSE.txt', 'model-provenance.json') {
        Copy-Item -LiteralPath (Join-Path $repository "Models/$asset") -Destination $modelDirectory
    }
    $modelScript = Join-Path $repository 'Tools/Assert-ModelAssets.ps1'
    & $modelScript -RootDirectory $modelRoot
    Set-Content -LiteralPath (Join-Path $modelDirectory 'YOLOX-LICENSE.txt') -Value 'Apache License Version 2.0 Megvii'
    Expect-Failure { & $modelScript -RootDirectory $modelRoot } 'complete upstream YOLOX license'
    Copy-Item -LiteralPath (Join-Path $repository 'Models/YOLOX-LICENSE.txt') -Destination $modelDirectory -Force
    Set-Content -LiteralPath (Join-Path $modelDirectory 'yolo11n.onnx') -Value 'unreviewed'
    Expect-Failure { & $modelScript -RootDirectory $modelRoot } 'Only the reviewed'
    Remove-Item -LiteralPath (Join-Path $modelDirectory 'yolo11n.onnx')
    [IO.File]::WriteAllBytes((Join-Path $modelDirectory 'yolox_nano.onnx'), [byte[]](0, 1, 2))
    Expect-Failure { & $modelScript -RootDirectory $modelRoot } 'checksum or length'
    Expect-Failure { & (Join-Path $repository 'scripts/b.ps1') -RequireAuthenticode } 'thumbprint is required'
    Write-Host 'Release gates passed: coverage boundaries, duplicate reports, XML safety, model allowlist/hash/license, required signing.'
} finally {
    $resolved = [IO.Path]::GetFullPath($temporary)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notlike 'vnotch-release-gates-*') { throw 'Unsafe temporary cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
