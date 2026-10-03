param(
    [string]$Filter = '*',
    [ValidateSet('short', 'medium', 'long', 'default')][string]$Job = 'short',
    [switch]$Counters
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Push-Location $repoRoot
try {
    dotnet build V-Notch.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Application Release build failed.' }
    $artifactRoot = Join-Path 'artifacts/service-optimization' ('run-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff'))
    $benchmarkArgs = @('--filter', $Filter, '--job', $Job, '--exporters', 'json', '--artifacts', $artifactRoot)
    if ($Counters) { $benchmarkArgs += '--counters' }
    dotnet run --project Tools/VNotch.Benchmarks/VNotch.Benchmarks.csproj -c Release -- @benchmarkArgs
    if ($LASTEXITCODE -ne 0) { throw 'Benchmark runner failed.' }
    # BDN may return zero even when individual benchmarks fail.
    if ($Filter -eq '*') {
        python Tools/VNotch.Benchmarks/check_results.py (Join-Path $artifactRoot 'results')
        if ($LASTEXITCODE -ne 0) { throw 'Performance regression gate failed.' }
    }
    Write-Output "Benchmark artifacts: $artifactRoot"
}
finally { Pop-Location }
