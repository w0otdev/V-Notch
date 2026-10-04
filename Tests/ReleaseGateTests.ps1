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
    Expect-Failure { & $coverageScript -ResultsDirectory $temporary } 'require.*line details'
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
    Expect-Failure { & $coverageScript -ResultsDirectory $temporary } 'No coverage report contains'
    Set-Content -LiteralPath (Join-Path $temporary 'run.cobertura.xml') -Value '<!DOCTYPE coverage [<!ENTITY x SYSTEM "file:///not-read">]><coverage>&x;</coverage>'
    Expect-Failure { & $coverageScript -ResultsDirectory $temporary } 'DTD|security reasons'

    # Distinct suites cover complementary lines of the same class. Extra assemblies
    # and the duplicate per-method line records must not inflate application coverage.
    $suite1 = '<coverage lines-valid="4" lines-covered="2"><packages><package name="V-Notch"><classes><class name="Example" filename="Services/Example.cs"><methods><method><lines><line number="1" hits="1"/></lines></method></methods><lines><line number="1" hits="1"/><line number="2" hits="1"/><line number="3" hits="0"/><line number="4" hits="0"/></lines></class></classes></package></packages></coverage>'
    $suite2 = $suite1.Replace('lines-valid="4" lines-covered="2"', 'lines-valid="5" lines-covered="3"').Replace('number="2" hits="1"', 'number="2" hits="0"').Replace('number="3" hits="0"', 'number="3" hits="1"').Replace('</packages>', '<package name="TestAssembly"><classes><class name="Test" filename="Test.cs"><lines><line number="1" hits="1"/></lines></class></classes></package></packages>')
    Set-Content -LiteralPath (Join-Path $temporary 'run.cobertura.xml') -Value $suite1
    Set-Content -LiteralPath $duplicate -Value $suite2
    & $coverageScript -ResultsDirectory $temporary -MinimumPercent 75
    Expect-Failure { & $coverageScript -ResultsDirectory $temporary -MinimumPercent 76 } 'below the required threshold'
    Copy-Item -LiteralPath $duplicate -Destination (Join-Path $temporary 'collector-copy.cobertura.xml')
    & $coverageScript -ResultsDirectory $temporary -MinimumPercent 75
    Remove-Item -LiteralPath $duplicate, (Join-Path $temporary 'collector-copy.cobertura.xml')
    Set-Content -LiteralPath (Join-Path $temporary 'run.cobertura.xml') -Value $suite2
    & $coverageScript -ResultsDirectory $temporary -MinimumPercent 50
    Expect-Failure { & $coverageScript -ResultsDirectory $temporary -MinimumPercent 51 } 'below the required threshold'

    $methodSuite = '<coverage lines-valid="3" lines-covered="1"><packages><package name="V-Notch"><classes><class name="Example" filename="Example.cs"><methods><method name="M" signature="()"><lines><line number="1" hits="1"/></lines></method><method name="N" signature="()"><lines><line number="1" hits="0"/><line number="2" hits="0"/></lines></method></methods><lines><line number="1" hits="1"/><line number="2" hits="0"/></lines></class></classes></package></packages></coverage>'
    Set-Content -LiteralPath (Join-Path $temporary 'run.cobertura.xml') -Value $methodSuite
    Set-Content -LiteralPath $duplicate -Value $methodSuite.Replace('name="M" signature="()"><lines><line number="1" hits="1"', 'name="M" signature="()"><lines><line number="1" hits="0"').Replace('name="N" signature="()"><lines><line number="1" hits="0"', 'name="N" signature="()"><lines><line number="1" hits="1"')
    Set-Content -LiteralPath (Join-Path $temporary 'unrelated.cobertura.xml') -Value '<coverage lines-valid="0" lines-covered="0"><packages><package name="TestAssembly"/></packages></coverage>'
    & $coverageScript -ResultsDirectory $temporary -MinimumPercent 66
    Expect-Failure { & $coverageScript -ResultsDirectory $temporary -MinimumPercent 67 } 'below the required threshold'

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
    Write-Host 'Release gates passed: coverage boundaries, merged suites/assemblies, duplicate reports, XML safety, model allowlist/hash/license, required signing.'
} finally {
    $resolved = [IO.Path]::GetFullPath($temporary)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notlike 'vnotch-release-gates-*') { throw 'Unsafe temporary cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
