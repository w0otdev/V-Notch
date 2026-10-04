$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path $artifactsRoot ('prepush-cleanup-test-' + [guid]::NewGuid().ToString('N'))))
if ([IO.Path]::GetDirectoryName($fixtureRoot) -ne $artifactsRoot) { throw 'Unsafe fixture root.' }
$previousDesktopMode = [Environment]::GetEnvironmentVariable('VNOTCH_RUN_DESKTOP_TESTS', 'Process')

# Run the actual prepush script in an isolated repository with stubbed dotnet commands.
function dotnet {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'test') {
        $index = [Array]::IndexOf($args, '--results-directory')
        if ($index -lt 0) { throw 'Missing coverage output path.' }
        $destination = $args[$index + 1]
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $destination 'coverage.xml') -Value '<coverage />'
        if (Test-Path -LiteralPath (Join-Path $fixtureRoot 'fail-tests')) { $global:LASTEXITCODE = 1 }
    }
}

try {
    New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'scripts') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'Tools') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'scripts/prepush.ps1') -Destination (Join-Path $fixtureRoot 'scripts/prepush.ps1')
    @'
param($ResultsDirectory)
if (-not (Test-Path -LiteralPath (Join-Path $ResultsDirectory 'coverage.xml'))) { throw 'Coverage fixture missing.' }
if (Test-Path -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) 'fail-coverage')) { throw 'Fixture coverage failure.' }
'@ | Set-Content -LiteralPath (Join-Path $fixtureRoot 'Tools/Assert-Coverage.ps1')

    foreach ($desktop in @($false, $true)) {
        foreach ($failure in @('none', 'tests', 'coverage')) {
            $testMarker = Join-Path $fixtureRoot 'fail-tests'
            $coverageMarker = Join-Path $fixtureRoot 'fail-coverage'
            if (Test-Path -LiteralPath $testMarker) { Remove-Item -LiteralPath $testMarker }
            if (Test-Path -LiteralPath $coverageMarker) { Remove-Item -LiteralPath $coverageMarker }
            if ($failure -eq 'tests') { Set-Content -LiteralPath $testMarker -Value 'fixture' }
            if ($failure -eq 'coverage') { Set-Content -LiteralPath $coverageMarker -Value 'fixture' }
            $failed = $false
            try {
                & (Join-Path $fixtureRoot 'scripts/prepush.ps1') -IncludeDesktopIntegration:$desktop *> $null
            } catch { $failed = $true }
            if ($failed -ne ($failure -ne 'none')) { throw "Unexpected result: desktop=$desktop, failure=$failure" }
            $coverageRoot = Join-Path $fixtureRoot 'artifacts'
            if ((Get-ChildItem -LiteralPath $coverageRoot -Directory -Filter 'prepush-coverage-*').Count -gt 0) {
                throw 'Coverage directory leaked.'
            }
            if ([string][Environment]::GetEnvironmentVariable('VNOTCH_RUN_DESKTOP_TESTS', 'Process') -ne [string]$previousDesktopMode) {
                throw 'Desktop test environment leaked.'
            }
        }
    }
    Write-Output 'Prepush cleanup: six success/failure scenarios passed.'
} finally {
    [Environment]::SetEnvironmentVariable('VNOTCH_RUN_DESKTOP_TESTS', $previousDesktopMode, 'Process')
    if ([IO.Path]::GetDirectoryName($fixtureRoot) -ne $artifactsRoot) { throw 'Unsafe fixture cleanup target.' }
    if (Test-Path -LiteralPath $fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
}
