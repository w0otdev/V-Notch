[CmdletBinding()]
param(
    [switch]$Full,
    [string]$BaseRef,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot

try {
    function Invoke-Step([string]$Title, [scriptblock]$Action) {
        Write-Host "==> $Title" -ForegroundColor Cyan
        & $Action
        if ($LASTEXITCODE -ne 0) {
            throw "Step failed ($LASTEXITCODE): $Title"
        }
    }

    function Get-ChangedFiles([string]$ExplicitBase) {
        $paths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

        $resolvedBase = $ExplicitBase
        if (-not $resolvedBase) {
            $upstream = (& git rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>$null)
            if ($LASTEXITCODE -eq 0 -and $upstream) {
                $resolvedBase = $upstream.Trim()
            } else {
                $null = (& git rev-parse --verify origin/main 2>$null)
                if ($LASTEXITCODE -eq 0) {
                    $resolvedBase = (& git merge-base HEAD origin/main 2>$null)
                    if ($LASTEXITCODE -ne 0) { $resolvedBase = $null }
                }
            }
        }

        if ($resolvedBase) {
            $committed = @(& git diff --name-only "$resolvedBase..HEAD" 2>$null)
            if ($LASTEXITCODE -eq 0) {
                foreach ($p in $committed) {
                    if (-not [string]::IsNullOrWhiteSpace($p)) { [void]$paths.Add($p.Replace('\', '/').Trim()) }
                }
            }
        } else {
            $headFiles = @(& git diff-tree --no-commit-id --name-only -r HEAD 2>$null)
            if ($LASTEXITCODE -eq 0) {
                foreach ($p in $headFiles) {
                    if (-not [string]::IsNullOrWhiteSpace($p)) { [void]$paths.Add($p.Replace('\', '/').Trim()) }
                }
            }
        }

        # Include local uncommitted edits when invoked manually before commit.
        $dirty = @(& git status --porcelain=v1 2>$null)
        if ($LASTEXITCODE -eq 0) {
            foreach ($line in $dirty) {
                if ($line.Length -gt 3) {
                    $entry = $line.Substring(3).Trim()
                    if ($entry -match ' -> (.+)$') { $entry = $Matches[1] }
                    [void]$paths.Add($entry.Replace('\', '/').Trim('"'))
                }
            }
        }

        return @($paths)
    }

    $changed = @(Get-ChangedFiles -ExplicitBase $BaseRef)
    Write-Host ("Pre-push check: {0} changed file(s) detected." -f $changed.Count)

    $packagedDocs = @(
        'CHANGELOG.md', 'PRIVACY_POLICY.md', 'PRIVACY_POLICY_VI.md',
        'TERMS_OF_SERVICE.md', 'TERMS_OF_SERVICE_VI.md', 'THIRD_PARTY_NOTICES.md',
        'docs/PRIVACY_POLICY.md', 'docs/PRIVACY_POLICY_VI.md',
        'docs/TERMS_OF_SERVICE.md', 'docs/TERMS_OF_SERVICE_VI.md',
        'docs/THIRD_PARTY_NOTICES.md', 'docs/licenses/Hardcodet.NotifyIcon.Wpf-MIT.txt'
    )
    $nonPackagedDocs = @('README.md', 'README_VI.md', 'CONTRIBUTING.md', 'GLOBAL_RELEASE_AUDIT.md', 'DEVOPS_PIPELINE_AUDIT.md')

    $needsBuildAndTests = $Full -or $changed.Count -eq 0 -or (@($changed | Where-Object {
        $p = $_
        ($packagedDocs -contains $p) -or
        $p.StartsWith('docs/PRIVACY_POLICY', [StringComparison]::OrdinalIgnoreCase) -or
        $p.StartsWith('docs/licenses/', [StringComparison]::OrdinalIgnoreCase) -or
        (($nonPackagedDocs -notcontains $p) -and -not $p.StartsWith('docs/', [StringComparison]::OrdinalIgnoreCase))
    }).Count -gt 0)

    if (-not $needsBuildAndTests) {
        Write-Host 'Docs-only change detected; skipping build and tests.' -ForegroundColor Green
        exit 0
    }

    if (Get-Command python -ErrorAction SilentlyContinue) {
        Invoke-Step 'Validate locales (Tools/validate_locales.py)' {
            & python Tools/validate_locales.py
        }
        if ($Full -or (@($changed | Where-Object { $_ -like '.github/*' }).Count -gt 0)) {
            Invoke-Step 'Run pipeline gate Python tests' {
                & python -m unittest discover -s .github/scripts -p 'test_*.py'
            }
        }
    } else {
        Write-Warning 'python not found on PATH; skipping validate_locales.py.'
    }

    if ($Full -or (@($changed | Where-Object { $_ -like 'Models/*' -or $_ -eq 'Tools/Assert-ModelAssets.ps1' }).Count -gt 0)) {
        Invoke-Step 'Verify model assets provenance' {
            & "$repoRoot/Tools/Assert-ModelAssets.ps1"
        }
    }

    $needsLockedRestore = $Full -or (@($changed | Where-Object {
        $_ -like '*.csproj' -or $_ -like '*packages.lock.json' -or $_ -like 'Directory.*.props' -or $_ -eq 'global.json'
    }).Count -gt 0)

    if ($needsLockedRestore) {
        Invoke-Step 'Restore solution (--locked-mode)' {
            & dotnet restore V-Notch.sln --locked-mode -nologo -v:q
        }
    }

    if (-not $SkipBuild) {
        Invoke-Step 'Build Release (-warnaserror)' {
            & dotnet build V-Notch.sln --configuration Release -warnaserror -nologo -v:q -clp:NoSummary -m -p:SkipNativeCompile=true
        }
    }

    if ($Full) {
        Invoke-Step 'Run full non-desktop test suite' {
            & dotnet test Tests/VNotch.Tests.csproj --configuration Release --no-build --no-restore `
                --filter 'Category!=DesktopIntegration' --blame-hang-timeout 60s --logger 'console;verbosity=minimal'
        }
    } else {
        $allTestFiles = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'Tests') -Filter '*Tests.cs' -File)
        $matchedClasses = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

        foreach ($path in $changed) {
            if ($path -match '^Tests/([^/]+)\.cs$') {
                $stem = $Matches[1]
                if ($stem -match 'Tests$') {
                    [void]$matchedClasses.Add($stem)
                }
                continue
            }

            if ($path -notmatch '\.(cs|xaml|cpp|h)$') { continue }

            $fileName = [IO.Path]::GetFileName($path)
            $base = $fileName -replace '\.(xaml\.cs|xaml|cs|cpp|h)$', ''
            if ($base.Length -lt 4) { continue }

            $shortStem = $base -replace '(?:Service|Helper|ViewModel|Control|Window|Module|Provider|Manager|Controller|Policy|Pipeline|Cache|Native)$', ''
            foreach ($testFile in $allTestFiles) {
                $testBase = [IO.Path]::GetFileNameWithoutExtension($testFile.Name)
                if ($testBase.IndexOf($base, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                    ($shortStem.Length -ge 5 -and $testBase.IndexOf($shortStem, [StringComparison]::OrdinalIgnoreCase) -ge 0)) {
                    [void]$matchedClasses.Add($testBase)
                }
            }
        }

        if ($matchedClasses.Count -gt 0) {
            $selected = @($matchedClasses | Sort-Object | Select-Object -First 30)
            $classFilter = ($selected | ForEach-Object { "FullyQualifiedName~.$_." }) -join '|'
            $filter = "Category!=DesktopIntegration&($classFilter)"
            Write-Host ("Matched {0} test class(es): {1}" -f $selected.Count, ($selected -join ', '))
            Invoke-Step 'Run affected unit tests' {
                & dotnet test Tests/VNotch.Tests.csproj --configuration Release --no-build --no-restore `
                    --filter $filter --blame-hang-timeout 60s --logger 'console;verbosity=minimal'
            }
        } else {
            Write-Host 'No directly matching *Tests.cs for changed files; build + locale gates passed.' -ForegroundColor Yellow
            Write-Host 'Tip: run "pwsh Tools/Test-PrePush.ps1 -Full" when touching shared core infrastructure.' -ForegroundColor DarkGray
        }
    }

    Write-Host ("Pre-push checks passed in {0:F1}s." -f $stopwatch.Elapsed.TotalSeconds) -ForegroundColor Green
} finally {
    Pop-Location
}
