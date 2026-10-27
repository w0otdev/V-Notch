<#
.SYNOPSIS
    Short alias for scripts\Invoke-PonytailPipeline.ps1 (Ponytail Auto Audit -> Code -> Review -> Benchmark).

.EXAMPLE
    .\scripts\p.ps1 "Tối ưu hiệu năng bộ nhớ và thread safety cho ClipboardModule"
.EXAMPLE
    .\scripts\p.ps1 -Prompt "Fix DWM flickering" -Scope "Windows,Native" -Runner PromptOnly
.EXAMPLE
    .\scripts\p.ps1 -BenchmarkOnly
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Prompt = "",

    [string]$Scope = ".",

    [ValidateSet("Auto", "Claude", "Agy", "PromptOnly", "BenchmarkOnly")]
    [string]$Runner = "Auto",

    [ValidateRange(1, 5)]
    [int]$MaxLoops = 2,

    [string]$TestFilter = "FullyQualifiedName~PerformanceBenchmarkTests|FullyQualifiedName~ScrubberPerformanceTests|FullyQualifiedName~ClipboardTrayOpeningPerformanceTests|FullyQualifiedName~ProgressViewModelPerformanceTests|FullyQualifiedName~PerformanceAuditRegressionTests",

    [ValidateRange(3, 300)]
    [int]$LiveBenchmarkSeconds = 10,

    [ValidateRange(0, 60)]
    [int]$WarmupSeconds = 2,

    [switch]$LaunchAppForBenchmark,

    [switch]$SkipLiveBenchmark,

    [switch]$BenchmarkOnly
)

$pipelineScript = Join-Path $PSScriptRoot "Invoke-PonytailPipeline.ps1"
& $pipelineScript @PSBoundParameters
