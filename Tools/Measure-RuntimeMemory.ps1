[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$TargetProcessId,
    [Parameter(Mandatory)][string]$Scenario,
    [ValidateRange(1, 3600)][int]$DurationSeconds = 120,
    [ValidateRange(20, 5000)][int]$IntervalMilliseconds = 100,
    [Parameter(Mandatory)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$process = Get-Process -Id $TargetProcessId
$samples = [Collections.Generic.List[object]]::new()
$started = $process.StartTime.ToUniversalTime()
$baselineLifetimePeak = $process.PeakWorkingSet64
$clock = [Diagnostics.Stopwatch]::StartNew()
$exited = $false
try {
    do {
        $process.Refresh()
        if ($process.HasExited) { $exited = $true; break }
        $samples.Add([ordered]@{
            ElapsedMs = $clock.ElapsedMilliseconds
            WorkingSetBytes = $process.WorkingSet64
            PrivateCommitBytes = $process.PrivateMemorySize64
            LifetimePeakWorkingSetBytes = $process.PeakWorkingSet64
        })
        Start-Sleep -Milliseconds $IntervalMilliseconds
    } while ($clock.Elapsed.TotalSeconds -lt $DurationSeconds)
} finally {
    $clock.Stop()
    $process.Dispose()
}
if ($samples.Count -eq 0) { throw 'The process exited before a memory sample could be recorded.' }
$result = [ordered]@{
    Scenario = $Scenario
    ProcessId = $TargetProcessId
    ProcessStartedUtc = $started.ToString('O')
    CapturedUtc = [DateTime]::UtcNow.ToString('O')
    ElapsedMs = $clock.ElapsedMilliseconds
    ProcessExited = $exited
    IntervalMilliseconds = $IntervalMilliseconds
    PhysicalMemoryBytes = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
    DisplayAdapters = @((Get-CimInstance Win32_VideoController).Name)
    BaselineLifetimePeakWorkingSetBytes = $baselineLifetimePeak
    LifetimePeakWorkingSetBytes = ($samples | Measure-Object LifetimePeakWorkingSetBytes -Maximum).Maximum
    SampledPeakWorkingSetBytes = ($samples | Measure-Object WorkingSetBytes -Maximum).Maximum
    SampledPeakPrivateCommitBytes = ($samples | Measure-Object PrivateCommitBytes -Maximum).Maximum
    Samples = $samples.ToArray()
}
$destination = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $destination -Encoding utf8
Write-Output $destination
