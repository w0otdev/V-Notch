using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using VNotch.Models;

namespace VNotch.Services;

public sealed class PerformanceDiagnosticService
{
    private static readonly Lazy<PerformanceDiagnosticService> _lazy =
        new(() => new PerformanceDiagnosticService());

    public static PerformanceDiagnosticService Instance => _lazy.Value;

    private readonly IntPtr _currentProcessHandle = Win32Interop.GetCurrentProcess();
    private readonly int _processorCount;
    private readonly List<DiagnosticLogEntry> _diagnosticLogs = new(64);
    private readonly object _lock = new();

    // CPU Tracking
    private ulong _lastProcTime;
    private long _lastProcTicks;
    private double _smoothedProcCpu;
    private ulong _lastSysIdle;
    private ulong _lastSysKernel;
    private ulong _lastSysUser;
    private double _smoothedGlobalCpu;

    // Memory Tracking
    private long _lastAllocatedBytes;
    private long _lastAllocSampleTicks;
    private double _smoothedAllocBytesPerSec;
    private ulong _lastWorkingSetBytes;
    private long _lastWorkingSetTicks;
    private int _lastGen0;
    private int _lastGen1;
    private int _lastGen2;

    // Dispatcher latency
    private double _lastDispatcherLatencyMs;
    private long _lastDispatcherPingTicks;
    private bool _dispatcherPingPending;

    // Thread count cache
    private long _lastThreadCountTicks;
    private int _cachedThreadCount = 1;

    // Alert debounce cooldowns
    private DateTime _lastFpsDropAlert = DateTime.MinValue;
    private DateTime _lastDispatcherAlert = DateTime.MinValue;
    private DateTime _lastGcGen2Alert = DateTime.MinValue;
    private DateTime _lastMemSurgeAlert = DateTime.MinValue;
    private DateTime _lastProcCpuAlert = DateTime.MinValue;
    private DateTime _lastGlobalCpuAlert = DateTime.MinValue;
    private DateTime _lastLowRamAlert = DateTime.MinValue;

    private PerformanceHealthLevel _currentHealthLevel = PerformanceHealthLevel.Nominal;
    private string _currentHealthSummary = "Performance Nominal";
    private DateTime _lastHealthAlertTime = DateTime.MinValue;

    private PerformanceDiagnosticService()
    {
        _processorCount = Math.Max(1, Environment.ProcessorCount);
        _lastAllocatedBytes = GC.GetTotalAllocatedBytes();
        _lastAllocSampleTicks = Stopwatch.GetTimestamp();
        _lastGen0 = GC.CollectionCount(0);
        _lastGen1 = GC.CollectionCount(1);
        _lastGen2 = GC.CollectionCount(2);

        AddLog(PerformanceHealthLevel.Nominal, "INIT", "Diagnostic engine active. Performance baseline established.");
    }

    public void PingDispatcher(Dispatcher dispatcher)
    {
        if (_dispatcherPingPending || dispatcher == null) return;
        _dispatcherPingPending = true;
        _lastDispatcherPingTicks = Stopwatch.GetTimestamp();

        dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            long now = Stopwatch.GetTimestamp();
            _lastDispatcherLatencyMs = (double)(now - _lastDispatcherPingTicks) / Stopwatch.Frequency * 1000.0;
            _dispatcherPingPending = false;
        }));
    }

    public PerformanceDebugSnapshot SampleSnapshot(
        double fps,
        int hz,
        double frameTimeMs,
        double netDown,
        double netUp)
    {
        long nowTicks = Stopwatch.GetTimestamp();
        DateTime now = DateTime.Now;

        // 1. Process CPU
        double procCpu = 0;
        try
        {
            if (Win32Interop.GetProcessTimes(_currentProcessHandle, out _, out _, out var procKernel, out var procUser))
            {
                ulong procTotalTime = procKernel.ToUInt64() + procUser.ToUInt64();
                if (_lastProcTicks > 0)
                {
                    ulong deltaProc = procTotalTime > _lastProcTime ? procTotalTime - _lastProcTime : 0;
                    double deltaWallSec = (double)(nowTicks - _lastProcTicks) / Stopwatch.Frequency;
                    if (deltaWallSec > 0.005)
                    {
                        double procSec = (double)deltaProc / 10_000_000.0;
                        double rawProcCpu = Math.Clamp((procSec / (deltaWallSec * _processorCount)) * 100.0, 0, 100);
                        _smoothedProcCpu = _lastProcTime == 0 ? rawProcCpu : (_smoothedProcCpu * 0.88 + rawProcCpu * 0.12);
                    }
                }
                _lastProcTime = procTotalTime;
                _lastProcTicks = nowTicks;
                procCpu = _smoothedProcCpu < 0.05 ? 0.0 : _smoothedProcCpu;
            }
        }
        catch { }

        // 2. Global CPU
        double globalCpu = 0;
        try
        {
            if (Win32Interop.GetSystemTimes(out var sysIdle, out var sysKernel, out var sysUser))
            {
                ulong idle = sysIdle.ToUInt64();
                ulong kernel = sysKernel.ToUInt64();
                ulong user = sysUser.ToUInt64();

                if (_lastSysIdle > 0)
                {
                    ulong deltaIdle = idle > _lastSysIdle ? idle - _lastSysIdle : 0;
                    ulong deltaKernel = kernel > _lastSysKernel ? kernel - _lastSysKernel : 0;
                    ulong deltaUser = user > _lastSysUser ? user - _lastSysUser : 0;
                    ulong deltaTotal = deltaKernel + deltaUser;

                    if (deltaTotal > 0)
                    {
                        double busy = deltaTotal > deltaIdle ? (double)(deltaTotal - deltaIdle) : 0;
                        double rawGlobalCpu = Math.Clamp((busy / deltaTotal) * 100.0, 0, 100);
                        _smoothedGlobalCpu = _lastSysIdle == 0 ? rawGlobalCpu : (_smoothedGlobalCpu * 0.88 + rawGlobalCpu * 0.12);
                    }
                }
                _lastSysIdle = idle;
                _lastSysKernel = kernel;
                _lastSysUser = user;
                globalCpu = _smoothedGlobalCpu;
            }
        }
        catch { }

        // 3. Process RAM (Working Set & Private Bytes)
        ulong procWorkingSet = 0;
        ulong procPrivateBytes = 0;
        try
        {
            var memCounters = new Win32Interop.PROCESS_MEMORY_COUNTERS_EX();
            memCounters.cb = (uint)Marshal.SizeOf<Win32Interop.PROCESS_MEMORY_COUNTERS_EX>();
            if (Win32Interop.GetProcessMemoryInfo(_currentProcessHandle, out memCounters, memCounters.cb))
            {
                procWorkingSet = (ulong)memCounters.WorkingSetSize;
                procPrivateBytes = (ulong)memCounters.PrivateUsage;
            }
        }
        catch { }

        // 4. Managed GC Memory & Allocations
        long managedHeapBytes = GC.GetTotalMemory(false);
        long currentTotalAlloc = GC.GetTotalAllocatedBytes();
        double allocDeltaSec = (double)(nowTicks - _lastAllocSampleTicks) / Stopwatch.Frequency;
        if (allocDeltaSec >= 0.2)
        {
            long allocDeltaBytes = currentTotalAlloc - _lastAllocatedBytes;
            double rate = allocDeltaBytes > 0 ? allocDeltaBytes / allocDeltaSec : 0;
            _smoothedAllocBytesPerSec = _smoothedAllocBytesPerSec * 0.7 + rate * 0.3;
            _lastAllocatedBytes = currentTotalAlloc;
            _lastAllocSampleTicks = nowTicks;
        }

        int gen0 = GC.CollectionCount(0);
        int gen1 = GC.CollectionCount(1);
        int gen2 = GC.CollectionCount(2);

        // 5. Threads & Handles (Cached/Win32 to prevent 750 object/sec allocations)
        if (_lastThreadCountTicks == 0 || (double)(nowTicks - _lastThreadCountTicks) / Stopwatch.Frequency >= 1.5)
        {
            try
            {
                using var curProc = Process.GetCurrentProcess();
                _cachedThreadCount = curProc.Threads.Count;
            }
            catch { }
            _lastThreadCountTicks = nowTicks;
        }
        int threadCount = _cachedThreadCount;

        int handleCount = 0;
        try
        {
            if (Win32Interop.GetProcessHandleCount(_currentProcessHandle, out uint hCount))
            {
                handleCount = (int)hCount;
            }
        }
        catch { }

        // 6. Global RAM Status
        ulong globalRamUsed = 0;
        ulong globalRamTotal = 0;
        ulong globalRamAvail = 0;
        double globalRamPercent = 0;
        try
        {
            var memStatus = new Win32Interop.MEMORYSTATUSEX_METRICS();
            memStatus.dwLength = (uint)Marshal.SizeOf<Win32Interop.MEMORYSTATUSEX_METRICS>();
            if (Win32Interop.GlobalMemoryStatusEx(ref memStatus))
            {
                globalRamTotal = memStatus.ullTotalPhys;
                globalRamAvail = memStatus.ullAvailPhys;
                globalRamUsed = memStatus.ullTotalPhys > memStatus.ullAvailPhys ? memStatus.ullTotalPhys - memStatus.ullAvailPhys : 0;
                globalRamPercent = Math.Clamp(memStatus.dwMemoryLoad, 0, 100);
            }
        }
        catch { }

        var (gpuName, vram) = GpuMonitorService.Instance.GetGpuInfo();
        var (procGpu, globalGpu) = GpuMonitorService.Instance.GetGpuUsage();

        // 7. Automated Anomaly Detection & Diagnostics
        DetectAnomalies(
            fps,
            hz,
            frameTimeMs,
            _lastDispatcherLatencyMs,
            procCpu,
            globalCpu,
            threadCount,
            procWorkingSet,
            managedHeapBytes,
            _smoothedAllocBytesPerSec,
            gen0,
            gen1,
            gen2,
            globalRamPercent,
            globalRamAvail,
            now);

        return new PerformanceDebugSnapshot
        {
            Fps = fps,
            RefreshRateHz = hz,
            FrameTimeMs = frameTimeMs,
            DispatcherLatencyMs = _lastDispatcherLatencyMs,
            GpuName = gpuName,
            DedicatedVramBytes = vram,
            ProcessCpuPercent = procCpu,
            ProcessThreadCount = threadCount,
            ProcessHandleCount = handleCount,
            ProcessWorkingSetBytes = procWorkingSet,
            ProcessPrivateBytes = procPrivateBytes,
            ManagedHeapBytes = managedHeapBytes,
            AllocBytesPerSec = _smoothedAllocBytesPerSec,
            GcGen0Count = gen0,
            GcGen1Count = gen1,
            GcGen2Count = gen2,
            ProcessGpuPercent = procGpu,
            GlobalCpuPercent = globalCpu,
            GlobalRamUsedBytes = globalRamUsed,
            GlobalRamTotalBytes = globalRamTotal,
            GlobalRamAvailBytes = globalRamAvail,
            GlobalRamPercent = globalRamPercent,
            GlobalGpuPercent = globalGpu,
            NetDownBytesPerSec = netDown,
            NetUpBytesPerSec = netUp,
            HealthLevel = _currentHealthLevel,
            HealthStatusSummary = _currentHealthSummary
        };
    }

    private void DetectAnomalies(
        double fps,
        int hz,
        double frameTimeMs,
        double dispatcherLatencyMs,
        double procCpu,
        double globalCpu,
        int threadCount,
        ulong procWorkingSet,
        long managedHeapBytes,
        double allocBytesPerSec,
        int gen0,
        int gen1,
        int gen2,
        double globalRamPercent,
        ulong globalRamAvail,
        DateTime now)
    {
        bool hasAlert = false;

        // A. Frame Drop / Render Pipeline Stall
        if (hz >= 50 && fps > 0 && fps < 35 && (now - _lastFpsDropAlert).TotalSeconds > 3.0)
        {
            _lastFpsDropAlert = now;
            hasAlert = true;
            _currentHealthLevel = PerformanceHealthLevel.Warning;
            _currentHealthSummary = $"FPS Drop: {fps:0} FPS (Frame time: {frameTimeMs:0.0}ms)";
            AddLog(PerformanceHealthLevel.Warning, "RENDER", $"Frame drop detected: {fps:0} FPS ({frameTimeMs:0.1}ms frame time). Render pipeline burdened.");
        }

        // B. UI Dispatcher Lag (Thread blocking)
        if (dispatcherLatencyMs > 75.0 && (now - _lastDispatcherAlert).TotalSeconds > 3.0)
        {
            _lastDispatcherAlert = now;
            hasAlert = true;
            var level = dispatcherLatencyMs > 150 ? PerformanceHealthLevel.Critical : PerformanceHealthLevel.Warning;
            _currentHealthLevel = level;
            _currentHealthSummary = $"UI Lag: {dispatcherLatencyMs:0}ms delay on Dispatcher";
            AddLog(level, "THREAD", $"UI Dispatcher queue stall: {dispatcherLatencyMs:0}ms latency. Main thread blocked by workload.");
        }

        // C. Full GC Gen 2 Collection (Warn only when heap is abnormally bloated > 60 MB)
        if (gen2 > _lastGen2 && (now - _lastGcGen2Alert).TotalSeconds > 3.0)
        {
            _lastGcGen2Alert = now;
            double heapMb = managedHeapBytes / 1024.0 / 1024.0;
            if (heapMb >= 60.0)
            {
                hasAlert = true;
                _currentHealthLevel = PerformanceHealthLevel.Warning;
                _currentHealthSummary = $"GC Gen 2 Collection ({heapMb:0.1} MB Heap)";
                AddLog(PerformanceHealthLevel.Warning, "GC", $"Full GC (Gen 2) collection triggered. Managed heap: {heapMb:0.1} MB.");
            }
        }
        _lastGen0 = gen0;
        _lastGen1 = gen1;
        _lastGen2 = gen2;

        // D. Rapid Memory Surge (> 40 MB/s allocation spike when working set is high)
        if (_lastWorkingSetTicks > 0)
        {
            double wsDeltaSec = (double)(Stopwatch.GetTimestamp() - _lastWorkingSetTicks) / Stopwatch.Frequency;
            if (wsDeltaSec >= 0.8)
            {
                long wsDelta = (long)procWorkingSet - (long)_lastWorkingSetBytes;
                double deltaMb = wsDelta / 1024.0 / 1024.0;
                double currentMb = procWorkingSet / 1024.0 / 1024.0;
                double mbPerSec = wsDeltaSec > 0 ? deltaMb / wsDeltaSec : 0;
                if (mbPerSec > 40.0 && deltaMb > 30.0 && currentMb > 180.0 && (now - _lastMemSurgeAlert).TotalSeconds > 4.0)
                {
                    _lastMemSurgeAlert = now;
                    hasAlert = true;
                    _currentHealthLevel = PerformanceHealthLevel.Warning;
                    _currentHealthSummary = $"Memory Surge: +{mbPerSec:0.0} MB/s (Total: {currentMb:0} MB)";
                    AddLog(PerformanceHealthLevel.Warning, "MEMORY", $"Rapid memory allocation surge: +{deltaMb:0.0} MB within {wsDeltaSec:0.1}s (+{mbPerSec:0.1} MB/s, Working Set: {currentMb:0.0} MB).");
                    MemoryOptimizerService.Instance.ScheduleTrim(1000);
                }
                _lastWorkingSetBytes = procWorkingSet;
                _lastWorkingSetTicks = Stopwatch.GetTimestamp();
            }
        }
        else
        {
            _lastWorkingSetBytes = procWorkingSet;
            _lastWorkingSetTicks = Stopwatch.GetTimestamp();
        }

        // E. High V-Notch CPU Usage
        if (procCpu > 25.0 && (now - _lastProcCpuAlert).TotalSeconds > 4.0)
        {
            _lastProcCpuAlert = now;
            hasAlert = true;
            var level = procCpu > 50.0 ? PerformanceHealthLevel.Critical : PerformanceHealthLevel.Warning;
            _currentHealthLevel = level;
            _currentHealthSummary = $"High V-Notch CPU: {procCpu:0.1}%";
            AddLog(level, "CPU", $"High CPU usage by V-Notch: {procCpu:0.1}%. Active threads: {threadCount}.");
        }


        // F. Global CPU Bottleneck (Other system apps hogging CPU)
        if (globalCpu > 92.0 && (now - _lastGlobalCpuAlert).TotalSeconds > 4.0)
        {
            _lastGlobalCpuAlert = now;
            hasAlert = true;
            _currentHealthLevel = PerformanceHealthLevel.Warning;
            _currentHealthSummary = $"Global CPU Bottleneck: {globalCpu:0}% (System Busy)";
            AddLog(PerformanceHealthLevel.Warning, "SYSTEM", $"System-wide CPU high load ({globalCpu:0}%). External applications consuming CPU.");
        }

        // G. System Memory Depleted (< 1.2 GB available)
        double availGb = globalRamAvail / 1024.0 / 1024.0 / 1024.0;
        if (globalRamPercent > 94.0 && availGb < 1.2 && (now - _lastLowRamAlert).TotalSeconds > 5.0)
        {
            _lastLowRamAlert = now;
            hasAlert = true;
            _currentHealthLevel = PerformanceHealthLevel.Critical;
            _currentHealthSummary = $"Low System RAM: {availGb:0.1} GB Available ({globalRamPercent:0}%)";
            AddLog(PerformanceHealthLevel.Critical, "SYSTEM", $"Critical low system memory: only {availGb:0.2} GB available ({globalRamPercent:0}% utilized).");
        }

        if (hasAlert)
        {
            _lastHealthAlertTime = now;
        }
        else if ((now - _lastHealthAlertTime).TotalSeconds > 5.0)
        {
            _currentHealthLevel = PerformanceHealthLevel.Nominal;
            _currentHealthSummary = "Performance Nominal";
        }
    }

    public void AddLog(PerformanceHealthLevel severity, string category, string message)
    {
        lock (_lock)
        {
            if (_diagnosticLogs.Count >= 60)
            {
                _diagnosticLogs.RemoveAt(0);
            }
            _diagnosticLogs.Add(new DiagnosticLogEntry(DateTime.Now, severity, category, message));
        }
    }

    public IReadOnlyList<DiagnosticLogEntry> GetRecentLogs()
    {
        lock (_lock)
        {
            return _diagnosticLogs.ToArray();
        }
    }

    public void ClearLogs()
    {
        lock (_lock)
        {
            _diagnosticLogs.Clear();
            AddLog(PerformanceHealthLevel.Nominal, "INFO", "Diagnostics log cleared.");
        }
    }
}
