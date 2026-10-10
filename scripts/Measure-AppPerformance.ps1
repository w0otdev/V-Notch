[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 2147483647)]
    [int]$AppProcessId,
    [ValidateRange(1, 3600)]
    [int]$DurationSeconds = 30,
    [ValidateRange(0, 120)]
    [int]$WarmupSeconds = 5,
    [ValidateRange(1, 10)]
    [int]$SampleIntervalSeconds = 1,
    [string]$OutputPath = 'artifacts/app-performance.json',
    [switch]$SkipGpuCounters
)

$ErrorActionPreference = 'Stop'
$taskProcess = Get-Process -Id $AppProcessId
$taskProcessName = $taskProcess.ProcessName
$taskStartTimeUtc = $taskProcess.StartTime.ToUniversalTime().ToString('o')
$taskGpuCounterPath = '\GPU Engine(pid_' + $AppProcessId + '_*)\Utilization Percentage'
$taskGpuAvailable = -not $SkipGpuCounters
$taskGpuStatus = if ($SkipGpuCounters) { 'Not requested' } else { 'Not sampled yet' }
$taskSamples = [Collections.Generic.List[object]]::new()
$taskClock = [Diagnostics.Stopwatch]::StartNew()
$taskLogicalProcessors = [Environment]::ProcessorCount
$taskExitReason = $null

Write-Host "Sampling $taskProcessName (PID $AppProcessId) after $WarmupSeconds s warmup; $DurationSeconds s measurement. Keep workload, display and refresh rate fixed."
if ($WarmupSeconds -gt 0) { Start-Sleep -Seconds $WarmupSeconds }
$taskProcess.Refresh()
$taskPreviousCpuSeconds = $taskProcess.TotalProcessorTime.TotalSeconds
$taskPreviousTimestamp = $taskClock.Elapsed.TotalSeconds
$taskMeasurementStart = $taskPreviousTimestamp

while ($taskClock.Elapsed.TotalSeconds - $taskMeasurementStart -lt $DurationSeconds) {
    Start-Sleep -Seconds $SampleIntervalSeconds
    $taskGpuSamples = @()
    if ($taskGpuAvailable) {
        try {
            # Each counter instance names its engine and PID. Keep instances
            # separate: adding parallel engines can exceed 100% and is not an
            # overall GPU utilization percentage. English counter names may be
            # unavailable on a localized Windows installation.
            $taskGpuResult = Get-Counter -Counter $taskGpuCounterPath -MaxSamples 1 -ErrorAction Stop
            $taskGpuSamples = @($taskGpuResult.CounterSamples | ForEach-Object {
                [pscustomobject]@{
                    Instance = $_.InstanceName
                    UtilizationPercent = $_.CookedValue
                    Status = $_.Status
                }
            })
            $taskGpuStatus = 'Per-engine samples captured; do not sum as overall GPU percent'
        }
        catch {
            $taskGpuAvailable = $false
            $taskGpuStatus = 'Unavailable: ' + $_.Exception.Message
        }
    }
    try {
        if ($taskProcess.HasExited) { $taskExitReason = 'Target process exited'; break }
        $taskProcess.Refresh()
        $taskNow = $taskClock.Elapsed.TotalSeconds
        $taskCpuSeconds = $taskProcess.TotalProcessorTime.TotalSeconds
        $taskElapsed = $taskNow - $taskPreviousTimestamp
        $taskCpuOneCorePercent = 100.0 * ($taskCpuSeconds - $taskPreviousCpuSeconds) / $taskElapsed
        $taskSamples.Add([pscustomobject]@{
            TimestampUtc = [DateTime]::UtcNow.ToString('o')
            ElapsedSeconds = $taskNow - $taskMeasurementStart
            CpuPercentOfMachine = $taskCpuOneCorePercent / $taskLogicalProcessors
            CpuPercentOfOneCore = $taskCpuOneCorePercent
            WorkingSetBytes = $taskProcess.WorkingSet64
            PrivateBytes = $taskProcess.PrivateMemorySize64
            HandleCount = $taskProcess.HandleCount
            ThreadCount = $taskProcess.Threads.Count
            GpuEngineSamples = $taskGpuSamples
        })
        $taskPreviousCpuSeconds = $taskCpuSeconds
        $taskPreviousTimestamp = $taskNow
    }
    catch {
        $taskExitReason = 'Process metrics unavailable: ' + $_.Exception.Message
        break
    }
}

$taskReport = [pscustomobject]@{
    ProcessId = $AppProcessId
    ProcessName = $taskProcessName
    ProcessStartTimeUtc = $taskStartTimeUtc
    LogicalProcessors = $taskLogicalProcessors
    WarmupSeconds = $WarmupSeconds
    RequestedDurationSeconds = $DurationSeconds
    ActualDurationSeconds = $taskClock.Elapsed.TotalSeconds - $taskMeasurementStart
    SampleIntervalSeconds = $SampleIntervalSeconds
    GpuCounterStatus = $taskGpuStatus
    ExitReason = $taskExitReason
    Scope = 'External process CPU/private bytes/working set and optional per-engine GPU counters; no FPS, managed heap, or display frame tracing'
    Samples = @($taskSamples.ToArray())
}
$taskOutputAbsolute = [IO.Path]::GetFullPath($OutputPath)
$taskOutputDirectory = [IO.Path]::GetDirectoryName($taskOutputAbsolute)
[IO.Directory]::CreateDirectory($taskOutputDirectory) | Out-Null
$taskReport | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $taskOutputAbsolute -Encoding UTF8
Write-Host "Saved $($taskSamples.Count) samples to $taskOutputAbsolute. GPU counters: $taskGpuStatus"
$taskProcess.Dispose()
