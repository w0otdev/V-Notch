param(
    [ValidateSet('All', 'Render', 'Tray', 'Report')][string]$Mode = 'All',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$benchmarkRepo = Split-Path -Parent $PSScriptRoot
$benchmarkArtifacts = Join-Path $benchmarkRepo 'artifacts'
New-Item -ItemType Directory -Force -Path $benchmarkArtifacts | Out-Null
$benchmarkProbe = Join-Path $benchmarkRepo 'Tools/GlassGpuProbe/bin/Release/net8.0-windows10.0.19041.0/win-x64/WpfProbe.exe'
function Invoke-Probe([string[]]$Arguments, [string]$Log) {
    & $benchmarkProbe @Arguments *> (Join-Path $benchmarkArtifacts $Log)
    if ($LASTEXITCODE -ne 0) { throw "Benchmark failed; inspect artifacts/$Log" }
}
function New-TrayRun([string]$Name, [string]$FixtureMode, [string]$RunMode) {
    $fixture = Join-Path $benchmarkArtifacts ('tray-benchmark-' + $Name + '-' + [guid]::NewGuid().ToString('N'))
    Invoke-Probe @($FixtureMode, $fixture) ('tray-benchmark-' + $Name + '-fixture.log')
    Invoke-Probe @($RunMode, $fixture) ('tray-benchmark-' + $Name + '.log')
}
Push-Location $benchmarkRepo
try {
    if ($Mode -ne 'Report') {
        if (!$SkipBuild) {
            & dotnet build Tools/GlassGpuProbe/WpfProbe.csproj -c Release --no-restore -v:minimal *> (Join-Path $benchmarkArtifacts 'benchmark-build.log')
            if ($LASTEXITCODE -ne 0) { throw 'Benchmark build failed; inspect artifacts/benchmark-build.log' }
        }
        if ($Mode -in @('All', 'Render')) { Invoke-Probe @('--benchmark') 'glass-benchmark.log' }
        if ($Mode -in @('All', 'Tray')) {
            New-TrayRun 'stress' '--tray-fixture' '--tray-benchmark'
            New-TrayRun 'default' '--tray-fixture-default' '--tray-default-benchmark'
            New-TrayRun 'solid' '--tray-fixture-default' '--tray-solid-benchmark'
        }
    }
    $hardware = [ordered]@{
        TimestampUtc = [DateTimeOffset]::UtcNow.ToString('o')
        Cpu = @(Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors)
        Gpus = @(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate)
        RamBytes = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
        Os = Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber
        GitHead = (& git rev-parse HEAD)
        NativeSha256 = (Get-FileHash -LiteralPath (Join-Path $benchmarkRepo 'Native/out/VNotch.Native.dll')).Hash
    }
    $hardware | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $benchmarkArtifacts 'benchmark-hardware.json') -Encoding utf8
    function Median([double[]]$Values) {
        $sorted = @($Values | Sort-Object)
        if (!$sorted.Count) { return 0 }
        $mid = [int][math]::Floor($sorted.Count / 2)
        if ($sorted.Count % 2) { return $sorted[$mid] }
        return ($sorted[$mid - 1] + $sorted[$mid]) / 2
    }
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('# Liquid Glass benchmark after optimization')
    $lines.Add('')
    $cpuNames = ($hardware.Cpu.Name -join '; ')
    $gpuNames = ($hardware.Gpus.Name -join '; ')
    $ramGiB = [math]::Round($hardware.RamBytes / 1GB, 1)
    $lines.Add("Machine: $cpuNames; reported physical RAM $ramGiB GiB; GPUs: $gpuNames. Windows / display / driver / binary details are in benchmark-hardware.json; DPI is in each raw result file.")
    $lines.Add('')
    $lines.Add('FPS means completed frames submitted through D3DImage.Unlock, not frames physically displayed. Latency spans capture/render, GPU completion and UI handoff; it excludes DWM/display latency. CPU is percent of the whole machine. Private commit includes managed/native/graphics allocations and is not VRAM. All numbers include benchmark instrumentation. There is no equivalent before-optimization run for this workload, so no speedup or app-wide RAM reduction is inferred.')
    $lines.Add('')
    $renderPath = Join-Path $benchmarkArtifacts 'glass-benchmark-results.json'
    if (Test-Path -LiteralPath $renderPath) {
        $render = Get-Content -Raw -LiteralPath $renderPath | ConvertFrom-Json
        if ($render.Failure) { throw $render.Failure }
        $summary = @($render.Runs | Group-Object { if ($_.Scenario -eq 'moving') { "$($_.Scenario)/$($_.Width)x$($_.Height)/$($_.TargetFps)/$($_.BlurPercent)" } else { $_.Scenario } } | ForEach-Object {
            $runs = @($_.Group); $first = $runs[0]
            [pscustomobject][ordered]@{
                Scenario = $first.Scenario; Region = if ($first.Scenario -eq 'resize-250ms') { '320x64 <-> 1200x600' } else { "$($first.Width)x$($first.Height)" }
                TargetFps = $first.TargetFps; BlurPercent = $first.BlurPercent; Repeats = $runs.Count
                FpsMedian = [math]::Round((Median $runs.SubmittedFps), 2)
                FpsMin = [math]::Round(($runs.SubmittedFps | Measure-Object -Minimum).Minimum, 2)
                FpsMax = [math]::Round(($runs.SubmittedFps | Measure-Object -Maximum).Maximum, 2)
                LatencyP95MedianMs = [math]::Round((Median $runs.LatencyP95Ms), 3)
                IntervalP95MedianMs = [math]::Round((Median $runs.IntervalP95Ms), 3)
                LateIntervalsMeanPercent = [math]::Round(($runs.LateIntervalsPercent | Measure-Object -Average).Average, 2)
                CpuMachineMeanPercent = [math]::Round(($runs.CpuMachinePercent | Measure-Object -Average).Average, 2)
                ManagedAllocMeanMiBs = [math]::Round(($runs.ManagedAllocMiBPerSecond | Measure-Object -Average).Average, 3)
                PrivatePeakMiB = [math]::Round(($runs.PrivatePeakMiB | Measure-Object -Maximum).Maximum, 2)
                Gen2Total = ($runs.Gen2 | Measure-Object -Sum).Sum
            }
        })
        $summary | Export-Csv -NoTypeInformation -LiteralPath (Join-Path $benchmarkArtifacts 'glass-benchmark-summary.csv')
        $lines.Add('## Capture + blur + refraction + WPF')
        $lines.Add('')
        $lines.Add('Physical-pixel regions; source crop moves one pixel after every submitted frame to force source rebuild and blur. A continuous 1 ms wake request stresses the FPS cap. Three measured repetitions per configuration, about 3 seconds each after 1.2 seconds warm-up. Resize samples last 4 seconds. FPS and per-run p95 below are medians across repeats, not pooled percentiles. Cases run in order in one process, retaining GPU capacity. The desktop and OS scheduling remain uncontrolled.')
        $lines.Add('')
        $lines.Add('| Workload / region | Blur | Target | FPS median (range) | Latency p95 ms | Interval p95 ms | Late intervals % | CPU % | Alloc MiB/s |')
        $lines.Add('| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |')
        foreach ($row in $summary) {
            $lines.Add("| $($row.Scenario) $($row.Region) | $($row.BlurPercent)% | $($row.TargetFps) | $($row.FpsMedian) ($($row.FpsMin)-$($row.FpsMax)) | $($row.LatencyP95MedianMs) | $($row.IntervalP95MedianMs) | $($row.LateIntervalsMeanPercent) | $($row.CpuMachineMeanPercent) | $($row.ManagedAllocMeanMiBs) |")
        }
        $lines.Add('')
        $lines.Add(('Cold renderer first frame: {0:N1} ms; private commit delta {1:N2} MiB; managed allocation {2:N3} MiB. This includes shader/device initialization.' -f $render.Startup.FirstFrameMs,$render.Startup.PrivateDeltaMiB,$render.Startup.ManagedAllocMiB))
        $lines.Add('Late intervals are submission gaps longer than 1.5 times the target interval; they are not measured display drops. The 120 and 144 targets vary materially with size/cadence, and even the very low blur setting has misses. These runs do not establish a shader-only bottleneck.')
        $lines.Add('')
    }
    foreach ($item in @(@('Default retention / liquid backdrop','tray-benchmark-default-results.json'), @('Default retention / solid backdrop control','tray-benchmark-solid-results.json'), @('1024-card stress / liquid backdrop','tray-benchmark-results.json'))) {
        $path = Join-Path $benchmarkArtifacts $item[1]
        if (!(Test-Path -LiteralPath $path)) { continue }
        $tray = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
        if ($tray.Failure) { throw $tray.Failure }
        $lines.Add('## ' + $item[0]); $lines.Add('')
        $lines.Add('Real ClipboardTray in a 900x420 DIP test window. Synthetic small .txt files and 24 images at 1800x1200; visible previews load through the production store. Separate process prepares the fixture, keeping fixture allocation out of measurement. Default retention is 500; the stress run raises it to 2000. The solid control replaces only the glass backdrop, not the full application theme. No forced GC is used. First-open duration includes entrance deferral and 500 ms settling, not solely UI blocking time.')
        $lines.Add('')
        $lines.Add('| Phase | Seconds | Private delta / peak MiB | Managed alloc MiB | Gen2 | Glass FPS | UI callback interval p95 ms | Preview cache MiB |')
        $lines.Add('| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |')
        foreach ($phase in $tray.Phases) {
            $lines.Add(('| {0} | {1:N2} | {2:N2} / {3:N2} | {4:N2} | {5} | {6:N1} | {7:N2} | {8:N2} |' -f $phase.Name,$phase.Seconds,$phase.PrivateDeltaMiB,$phase.PrivatePeakMiB,$phase.ManagedAllocatedMiB,$phase.Gen2,$phase.GlassFps,$phase.UiIntervalP95Ms,$phase.PreviewCacheMiB))
        }
        $lines.Add('')
        $lines.Add('Tray settling was measured with a CompositionTarget.Rendering observer attached. That observer can drive composition; this row is not a measurement of app idle without diagnostics. Absolute process memory includes test-host/WPF initialization and should not be compared to the full V-Notch process as a claimed RAM saving.')
        $lines.Add('')
    }
    $defaultPath = Join-Path $benchmarkArtifacts 'tray-benchmark-default-results.json'
    $solidPath = Join-Path $benchmarkArtifacts 'tray-benchmark-solid-results.json'
    if ((Test-Path -LiteralPath $defaultPath) -and (Test-Path -LiteralPath $solidPath)) {
        $default = Get-Content -Raw -LiteralPath $defaultPath | ConvertFrom-Json
        $solid = Get-Content -Raw -LiteralPath $solidPath | ConvertFrom-Json
        $first = $default.Phases | Where-Object {$_.Name -like 'first-open*'} | Select-Object -First 1
        $solidFirst = $solid.Phases | Where-Object {$_.Name -like 'first-open*'} | Select-Object -First 1
        $scroll = $default.Phases | Where-Object {$_.Name -eq 'image-scroll-1'} | Select-Object -First 1
        $solidScroll = $solid.Phases | Where-Object {$_.Name -eq 'image-scroll-1'} | Select-Object -First 1
        $lines.Add('## Remaining bottlenecks')
        $lines.Add('')
        $lines.Add(('The default 500-card liquid run allocated {0:N2} MiB managed and increased private commit by {1:N2} MiB during first opening, with {2} Gen2 collections. The solid control allocated {3:N2} MiB managed with {4} Gen2 collections during the same opening workload.' -f $first.ManagedAllocatedMiB,$first.PrivateDeltaMiB,$first.Gen2,$solidFirst.ManagedAllocatedMiB,$solidFirst.Gen2))
        $lines.Add(('The first image sweep produced {0} Gen2 collections in {1:N2} s with glass; the solid control also produced {2} in {3:N2} s. This reproduces substantial content/preview-path pressure alongside the glass renderer. An allocation trace is needed to identify the allocating stacks and reason for full collections; the benchmark does not establish the individual cause.' -f $scroll.Gen2,$scroll.Seconds,$solidScroll.Gen2,$solidScroll.Seconds))
        $lines.Add('The 24-image fixture exceeds the 8 MiB preview budget: 17 decoded previews occupy about 7.63 MiB. The cache stays bounded, but revisiting this dataset still causes churn. First-open process growth and repeated scroll allocations remain relevant even after the GPU handoff optimization. No claim that the File Tray memory issue is resolved is supported by these measurements.')
        $lines.Add('')
    }
    $lines.Add('## Reproduce')
    $lines.Add('')
    $lines.Add('Run scripts/Invoke-GlassBenchmark.ps1 -Mode All from the repository. It displays temporary test windows, creates isolated fixtures under artifacts, saves raw JSON/logs plus a render CSV, and writes this report. It does not change app settings or clipboard content. Use -Mode Render or -Mode Tray for a subset; -Mode Report regenerates this summary. Fixtures remain under artifacts for inspection.')
    $reportPath = Join-Path $benchmarkArtifacts 'glass-benchmark-report.md'
    $lines | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Output $reportPath
}
finally { Pop-Location }
