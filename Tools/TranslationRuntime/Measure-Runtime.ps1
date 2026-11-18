param(
    [Parameter(Mandatory=$true)][string]$ModelDirectory,
    [Parameter(Mandatory=$true)][string]$RuntimeDll,
    [Parameter(Mandatory=$true)][string]$OutputJson,
    [int]$Threads = 4,
    [int]$Repetitions = 3,
    [string]$BaselineJson = '',
    [switch]$Fast
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Test-Runtime.ps1') -ModelDirectory $ModelDirectory -RuntimeDll $RuntimeDll -Threads $Threads -Fast:$Fast -DefinitionsOnly
$ModelDirectory = (Resolve-Path -LiteralPath $ModelDirectory).Path
$process = [Diagnostics.Process]::GetCurrentProcess()
function MemorySnapshot {
    [GC]::Collect(); [GC]::WaitForPendingFinalizers(); [GC]::Collect()
    $process.Refresh()
    @{ workingSetMiB=$process.WorkingSet64/1MB; privateMiB=$process.PrivateMemorySize64/1MB; peakMiB=$process.PeakWorkingSet64/1MB }
}
$longText = @'
The project team met early in the morning to review the results of the first trial. Several participants had tested the application on their own computers and brought detailed notes about what worked well and what remained confusing. They agreed that the next version should focus on making ordinary tasks easier, especially for people who use a small laptop while traveling.
During the discussion, a designer described how she prepares documents in several languages. She often copies a short paragraph from an email, checks the meaning, and then continues writing her reply. Opening a separate website for every sentence interrupts her concentration. She would prefer a small window that appears near the selected text and disappears when she finishes reading.
Another participant asked about battery life. His computer already runs a browser, a video call and a large spreadsheet throughout the day. A useful translation feature should respond quickly without keeping the processor busy when nobody is using it. The team decided to measure both active performance and the amount of memory retained between requests before changing the default settings.
They also discussed quality. A fast answer is helpful only when it preserves the meaning of the source. Names, addresses and important details must remain recognizable. Longer passages need enough context, while independent lines should stay in their original order. Nobody wanted a speed improvement that silently omitted part of a message or replaced a difficult sentence with a guess.
After the meeting, each participant received a different sample to test. They planned to compare the translated text, record any unexpected behavior, and report the time required on their own hardware. The next review would use those observations to decide whether the proposed changes were ready for daily use.
'@
$cases = @(
    @{name='short_en_vi';text='Where is the nearest train station?';target='vi'},
    @{name='short_ja_fr';text='明日の会議は何時に始まりますか？';target='fr'},
    @{name='short_ar_en';text='أين يمكنني شراء تذكرة القطار؟';target='en'},
    @{name='long_unique';text=$longText.Replace("`r",'').Replace("`n",' ');target='vi'},
    @{name='long_repeated';text=('The meeting starts tomorrow morning. Please check the documents before you leave. We need to confirm the train schedule and the address of the hotel. Thank you for your help with the arrangements. ' * 10);target='vi'},
    @{name='multiline';text="  Open the document.`r`nSave your changes.`r`nPlease check the train schedule.`r`nWhere is the nearest bus station?`r`n`r`nThank you for your help.`r`nClose the window.`r`nThe restaurant is open every day.`r`nWe will discuss the results tomorrow.`t";target='vi'},
    @{name='repeated_lines';text=(('Please check the train schedule.' + "`r`n") * 8);target='vi'}
)
$baseline = if ($BaselineJson) { Get-Content -LiteralPath $BaselineJson -Raw | ConvertFrom-Json } else { $null }
$report = [ordered]@{ threads=$Threads; logicalProcessors=[Environment]::ProcessorCount; repetitions=$Repetitions; baseline=MemorySnapshot; cases=@() }
[LiteRuntimeProbe]::Load((Resolve-Path -LiteralPath $RuntimeDll).Path, $false)
$timer = [Diagnostics.Stopwatch]::StartNew()
$status = [LiteRuntimeProbe]::Start($ModelDirectory, $Threads)
if ($status -ne 0) { throw "Load failed: $status" }
if ($Fast) { [LiteRuntimeProbe]::Natural($false) }
$report.mode = if ($Fast) { 'fast' } else { 'natural' }
$report.loadMs = $timer.Elapsed.TotalMilliseconds
$report.ready = MemorySnapshot
try {
    foreach ($case in $cases) {
        $times = @(); $cpuTimes = @(); $result = ''
        for ($repeat=0; $repeat -lt $Repetitions; $repeat++) {
            $process.Refresh(); $cpuStart = $process.TotalProcessorTime.TotalMilliseconds; $timer.Restart()
            $result = [LiteRuntimeProbe]::Run($case.text, $case.target, -1, 65536, 0)
            $times += $timer.Elapsed.TotalMilliseconds; $process.Refresh()
            $cpuTimes += $process.TotalProcessorTime.TotalMilliseconds - $cpuStart
        }
        $median = ($times | Sort-Object)[[int][Math]::Floor($times.Count/2)]
        $old = if ($baseline) { $baseline.cases | Where-Object name -eq $case.name } else { $null }
        $row = [ordered]@{ name=$case.name; inputChars=$case.text.Length; medianMs=[math]::Round($median,1); totalCpuMs=[math]::Round(($cpuTimes | Measure-Object -Sum).Sum,1); samplesMs=$times; output=$result }
        if ($old) { $row.speedup=[math]::Round($old.medianMs/$median,2); $row.sameOutput=$old.output -ceq $result }
        $report.cases += $row
        [pscustomobject]$row | Select-Object name,inputChars,medianMs,totalCpuMs,speedup,sameOutput | ConvertTo-Json -Compress
    }
    $report.active = MemorySnapshot
    Start-Sleep -Milliseconds 500
    $process.Refresh(); $cpuStart = $process.TotalProcessorTime.TotalMilliseconds; $timer.Restart()
    Start-Sleep -Seconds 2
    $process.Refresh()
    $report.idleCpuPercent = 100*($process.TotalProcessorTime.TotalMilliseconds-$cpuStart)/$timer.Elapsed.TotalMilliseconds/[Environment]::ProcessorCount
} finally { [LiteRuntimeProbe]::Stop() }
$report.released = MemorySnapshot
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputJson -Encoding utf8
@{loadMs=[math]::Round($report.loadMs,1); readyMiB=[math]::Round($report.ready.workingSetMiB-$report.baseline.workingSetMiB,1); peakMiB=[math]::Round($report.active.peakMiB-$report.baseline.workingSetMiB,1); releasedMiB=[math]::Round($report.released.workingSetMiB-$report.baseline.workingSetMiB,1); idleCpuPercent=[math]::Round($report.idleCpuPercent,2)} | ConvertTo-Json -Compress
