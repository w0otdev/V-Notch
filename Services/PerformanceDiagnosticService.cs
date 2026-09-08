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

    private static readonly uint MemCountersCb = (uint)Marshal.SizeOf<Win32Interop.PROCESS_MEMORY_COUNTERS_EX>();
    private static readonly uint MemStatusCb = (uint)Marshal.SizeOf<Win32Interop.MEMORYSTATUSEX_METRICS>();
    private readonly Process _currentProcess = Process.GetCurrentProcess();
    private readonly IntPtr _currentProcessHandle = Win32Interop.GetCurrentProcess();
    private readonly int _processorCount;
    private readonly List<DiagnosticLogEntry> _diagnosticLogs = new(64);
    private readonly List<DiagnosticLogEntry> _serviceLogs = new(256);
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
    private readonly long _serviceStartTicks = Stopwatch.GetTimestamp();

    private PerformanceDiagnosticService()
    {
        _processorCount = Math.Max(1, Environment.ProcessorCount);
        _lastAllocatedBytes = GC.GetTotalAllocatedBytes();
        _lastAllocSampleTicks = Stopwatch.GetTimestamp();
        _lastGen2 = GC.CollectionCount(2);

        AddLog(PerformanceHealthLevel.Nominal, "INIT", "Diagnostic engine active. Performance baseline established.");
        AddServiceLog(PerformanceHealthLevel.Nominal, "INIT", "Service logging engine active.");

        // Subscribe to RuntimeLog to receive real-time operational logs from all background services
        RuntimeLog.EntryWritten += (level, category, message) =>
        {
            var severity = level switch
            {
                LogLevel.Error => PerformanceHealthLevel.Critical,
                LogLevel.Warn => PerformanceHealthLevel.Warning,
                _ => PerformanceHealthLevel.Nominal
            };
            AddServiceLog(severity, category, message);
        };
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

        double procCpu = SampleProcessCpu(nowTicks);
        double globalCpu = SampleGlobalCpu();

        SampleProcessMemory(out ulong procWorkingSet, out ulong procPrivateBytes);
        SampleManagedMemory(nowTicks, out long managedHeapBytes, out int gen0, out int gen1, out int gen2);
        SampleThreadAndHandleCounts(nowTicks, out int threadCount, out int handleCount);
        SampleGlobalMemory(out ulong globalRamUsed, out ulong globalRamTotal, out ulong globalRamAvail, out double globalRamPercent);

        var (gpuName, vram) = GpuMonitorService.Instance.GetGpuInfo();
        var (procGpu, globalGpu) = GpuMonitorService.Instance.GetGpuUsage();

        var metrics = new AnomalyMetrics
        {
            Fps = fps,
            Hz = hz,
            FrameTimeMs = frameTimeMs,
            DispatcherLatencyMs = _lastDispatcherLatencyMs,
            ProcCpu = procCpu,
            GlobalCpu = globalCpu,
            ThreadCount = threadCount,
            ProcWorkingSet = procWorkingSet,
            ManagedHeapBytes = managedHeapBytes,
            Gen2 = gen2,
            GlobalRamPercent = globalRamPercent,
            GlobalRamAvail = globalRamAvail
        };

        DetectAnomalies(in metrics, now);

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

    private static ulong SafeDelta(ulong current, ulong previous) =>
        current > previous ? current - previous : 0;

    private double SampleProcessCpu(long nowTicks)
    {
        try
        {
            if (!Win32Interop.GetProcessTimes(_currentProcessHandle, out _, out _, out var procKernel, out var procUser))
            {
                return 0;
            }

            ulong procTotalTime = procKernel.ToUInt64() + procUser.ToUInt64();
            if (_lastProcTicks > 0)
            {
                ulong deltaProc = SafeDelta(procTotalTime, _lastProcTime);
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
            return _smoothedProcCpu < 0.05 ? 0.0 : _smoothedProcCpu;
        }
        catch (Exception)
        {
            // Best-effort process CPU sampling; ignore diagnostic query failures
            return 0;
        }
    }

    private double SampleGlobalCpu()
    {
        try
        {
            if (!Win32Interop.GetSystemTimes(out var sysIdle, out var sysKernel, out var sysUser))
            {
                return 0;
            }

            ulong idle = sysIdle.ToUInt64();
            ulong kernel = sysKernel.ToUInt64();
            ulong user = sysUser.ToUInt64();

            if (_lastSysIdle > 0)
            {
                ulong deltaIdle = SafeDelta(idle, _lastSysIdle);
                ulong deltaTotal = SafeDelta(kernel, _lastSysKernel) + SafeDelta(user, _lastSysUser);

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
            return _smoothedGlobalCpu;
        }
        catch (Exception)
        {
            // Best-effort system CPU sampling; ignore diagnostic query failures
            return 0;
        }
    }

    private void SampleProcessMemory(out ulong procWorkingSet, out ulong procPrivateBytes)
    {
        procWorkingSet = 0;
        procPrivateBytes = 0;
        try
        {
            if (Win32Interop.GetProcessMemoryInfo(_currentProcessHandle, out var memCounters, MemCountersCb))
            {
                procWorkingSet = (ulong)memCounters.WorkingSetSize;
                procPrivateBytes = (ulong)memCounters.PrivateUsage;
            }
        }
        catch (Exception)
        {
            // Best-effort process memory sampling; ignore query failures
        }
    }

    private void SampleManagedMemory(
        long nowTicks,
        out long managedHeapBytes,
        out int gen0,
        out int gen1,
        out int gen2)
    {
        managedHeapBytes = GC.GetTotalMemory(false);
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

        gen0 = GC.CollectionCount(0);
        gen1 = GC.CollectionCount(1);
        gen2 = GC.CollectionCount(2);
    }

    private void SampleThreadAndHandleCounts(long nowTicks, out int threadCount, out int handleCount)
    {
        if (_lastThreadCountTicks == 0 || (double)(nowTicks - _lastThreadCountTicks) / Stopwatch.Frequency >= 2.0)
        {
            try
            {
                _cachedThreadCount = _currentProcess.Threads.Count;
            }
            catch (Exception)
            {
                // Best-effort thread count sampling; ignore process inspection errors
            }
            _lastThreadCountTicks = nowTicks;
        }
        threadCount = _cachedThreadCount;

        handleCount = 0;
        try
        {
            if (Win32Interop.GetProcessHandleCount(_currentProcessHandle, out uint hCount))
            {
                handleCount = (int)hCount;
            }
        }
        catch (Exception)
        {
            // Best-effort handle count sampling; ignore query errors
        }
    }

    private static void SampleGlobalMemory(
        out ulong globalRamUsed,
        out ulong globalRamTotal,
        out ulong globalRamAvail,
        out double globalRamPercent)
    {
        globalRamUsed = 0;
        globalRamTotal = 0;
        globalRamAvail = 0;
        globalRamPercent = 0;
        try
        {
            var memStatus = new Win32Interop.MEMORYSTATUSEX_METRICS { dwLength = MemStatusCb };
            if (Win32Interop.GlobalMemoryStatusEx(ref memStatus))
            {
                globalRamTotal = memStatus.ullTotalPhys;
                globalRamAvail = memStatus.ullAvailPhys;
                globalRamUsed = memStatus.ullTotalPhys > memStatus.ullAvailPhys ? memStatus.ullTotalPhys - memStatus.ullAvailPhys : 0;
                globalRamPercent = Math.Clamp(memStatus.dwMemoryLoad, 0, 100);
            }
        }
        catch (Exception)
        {
            // Best-effort global memory status query; ignore system info errors
        }
    }

    private void DetectAnomalies(in AnomalyMetrics m, DateTime now)
    {
        bool hasAlert = false;
        double processAgeSec = (double)(Stopwatch.GetTimestamp() - _serviceStartTicks) / Stopwatch.Frequency;

        if (CheckFrameDrops(m, now)) hasAlert = true;
        if (CheckDispatcherLag(m, processAgeSec, now)) hasAlert = true;
        if (CheckGcGen2(m, now)) hasAlert = true;
        if (CheckMemorySurge(m, processAgeSec, now)) hasAlert = true;
        if (CheckCpuBottlenecks(m, now)) hasAlert = true;
        if (CheckSystemMemory(m, now)) hasAlert = true;

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

    private bool CheckFrameDrops(in AnomalyMetrics m, DateTime now)
    {
        double targetFrameBudgetMs = m.Hz > 0 ? 1000.0 / m.Hz : 16.6;
        if (m.Hz >= 50 && m.Fps > 0 && m.Fps < (m.Hz * 0.45) &&
            m.FrameTimeMs > (targetFrameBudgetMs * 2.5) && m.FrameTimeMs <= 100.0 &&
            (now - _lastFpsDropAlert).TotalSeconds > 4.0)
        {
            _lastFpsDropAlert = now;
            _currentHealthLevel = PerformanceHealthLevel.Warning;
            _currentHealthSummary = $"FPS Drop: {m.Fps:0} FPS ({m.FrameTimeMs:0.0}ms frame time)";
            AddLog(PerformanceHealthLevel.Warning, "BOTTLENECK-FPS",
                $"Frame drop ({m.Fps:0} FPS, {m.FrameTimeMs:0.1}ms frame time). Cause: Render composition pipeline exceeded target {targetFrameBudgetMs:0.1}ms frame budget (DWM composition or GPU refraction overhead).");
            return true;
        }

        return false;
    }

    private bool CheckDispatcherLag(in AnomalyMetrics m, double processAgeSec, DateTime now)
    {
        if (processAgeSec >= 10.0 && m.DispatcherLatencyMs > 75.0 && (now - _lastDispatcherAlert).TotalSeconds > 3.0)
        {
            _lastDispatcherAlert = now;
            var level = m.DispatcherLatencyMs > 150 ? PerformanceHealthLevel.Critical : PerformanceHealthLevel.Warning;
            _currentHealthLevel = level;
            _currentHealthSummary = $"UI Lag: {m.DispatcherLatencyMs:0}ms Dispatcher delay";
            AddLog(level, "BOTTLENECK-UI",
                $"UI Dispatcher queue lag ({m.DispatcherLatencyMs:0}ms latency). Cause: Main UI thread was blocked by synchronous layout, window resize, or heavy event callbacks.");
            return true;
        }

        return false;
    }

    private bool CheckGcGen2(in AnomalyMetrics m, DateTime now)
    {
        if (m.Gen2 > _lastGen2 && (now - _lastGcGen2Alert).TotalSeconds > 3.0)
        {
            _lastGcGen2Alert = now;
            double heapMb = m.ManagedHeapBytes / 1024.0 / 1024.0;
            if (heapMb >= 60.0)
            {
                _currentHealthLevel = PerformanceHealthLevel.Warning;
                _currentHealthSummary = $"GC Gen 2 Collection ({heapMb:0.1} MB Heap)";
                AddLog(PerformanceHealthLevel.Warning, "BOTTLENECK-GC",
                    $"Full GC Gen 2 collection triggered ({heapMb:0.1} MB managed heap). Cause: Large Object Heap compaction or high temporary object churn.");
                _lastGen2 = m.Gen2;
                return true;
            }
        }

        _lastGen2 = m.Gen2;
        return false;
    }

    private bool CheckMemorySurge(in AnomalyMetrics m, double processAgeSec, DateTime now)
    {
        if (_lastWorkingSetTicks > 0)
        {
            double wsDeltaSec = (double)(Stopwatch.GetTimestamp() - _lastWorkingSetTicks) / Stopwatch.Frequency;
            if (wsDeltaSec >= 1.5)
            {
                long wsDelta = (long)m.ProcWorkingSet - (long)_lastWorkingSetBytes;
                double deltaMb = wsDelta / 1024.0 / 1024.0;
                double currentMb = m.ProcWorkingSet / 1024.0 / 1024.0;
                double mbPerSec = wsDeltaSec > 0 ? deltaMb / wsDeltaSec : 0;
                if (processAgeSec >= 10.0 && mbPerSec > 25.0 && deltaMb > 30.0 && currentMb > 180.0 && (now - _lastMemSurgeAlert).TotalSeconds > 4.0)
                {
                    _lastMemSurgeAlert = now;
                    _currentHealthLevel = PerformanceHealthLevel.Warning;
                    _currentHealthSummary = $"Memory Surge: +{mbPerSec:0.0} MB/s (Total: {currentMb:0} MB)";
                    AddLog(PerformanceHealthLevel.Warning, "BOTTLENECK-RAM",
                        $"Rapid memory allocation surge (+{deltaMb:0.0} MB within {wsDeltaSec:0.1}s, +{mbPerSec:0.1} MB/s, total {currentMb:0.0} MB). Cause: Heavy visual tree instantiation, shell icon extraction, or media artwork cache.");
                    MemoryOptimizerService.Instance.ScheduleTrim(1000);
                    _lastWorkingSetBytes = m.ProcWorkingSet;
                    _lastWorkingSetTicks = Stopwatch.GetTimestamp();
                    return true;
                }
                _lastWorkingSetBytes = m.ProcWorkingSet;
                _lastWorkingSetTicks = Stopwatch.GetTimestamp();
            }
        }
        else
        {
            _lastWorkingSetBytes = m.ProcWorkingSet;
            _lastWorkingSetTicks = Stopwatch.GetTimestamp();
        }

        return false;
    }

    private bool CheckCpuBottlenecks(in AnomalyMetrics m, DateTime now)
    {
        bool alert = false;
        if (m.ProcCpu > 25.0 && (now - _lastProcCpuAlert).TotalSeconds > 4.0)
        {
            _lastProcCpuAlert = now;
            alert = true;
            var level = m.ProcCpu > 50.0 ? PerformanceHealthLevel.Critical : PerformanceHealthLevel.Warning;
            _currentHealthLevel = level;
            _currentHealthSummary = $"High V-Notch CPU: {m.ProcCpu:0.1}%";
            AddLog(level, "BOTTLENECK-CPU",
                $"High CPU usage by V-Notch ({m.ProcCpu:0.1}% across {m.ThreadCount} threads). Cause: Active liquid glass refraction, shader convolution, or rapid polling loops.");
        }

        if (m.GlobalCpu > 92.0 && (now - _lastGlobalCpuAlert).TotalSeconds > 4.0)
        {
            _lastGlobalCpuAlert = now;
            alert = true;
            _currentHealthLevel = PerformanceHealthLevel.Warning;
            _currentHealthSummary = $"Global CPU Bottleneck: {m.GlobalCpu:0}% (System Busy)";
            AddLog(PerformanceHealthLevel.Warning, "BOTTLENECK-SYS",
                $"System-wide CPU high load ({m.GlobalCpu:0}%). Cause: External Windows background applications/processes saturating CPU cores.");
        }

        return alert;
    }

    private bool CheckSystemMemory(in AnomalyMetrics m, DateTime now)
    {
        double availGb = m.GlobalRamAvail / 1024.0 / 1024.0 / 1024.0;
        if (m.GlobalRamPercent > 94.0 && availGb < 1.2 && (now - _lastLowRamAlert).TotalSeconds > 5.0)
        {
            _lastLowRamAlert = now;
            _currentHealthLevel = PerformanceHealthLevel.Critical;
            _currentHealthSummary = $"Low System RAM: {availGb:0.1} GB Available ({m.GlobalRamPercent:0}%)";
            AddLog(PerformanceHealthLevel.Critical, "BOTTLENECK-SYS",
                $"Critical low system memory ({availGb:0.2} GB available, {m.GlobalRamPercent:0}% utilized). Cause: Global OS physical memory exhaustion across running apps.");
            return true;
        }

        return false;
    }

    public void AddLog(PerformanceHealthLevel severity, string category, string message)
    {
        lock (_lock)
        {
            if (_diagnosticLogs.Count >= 200)
            {
                _diagnosticLogs.RemoveAt(0);
            }
            _diagnosticLogs.Add(new DiagnosticLogEntry(DateTime.Now, severity, category, message));
        }
    }

    public void AddServiceLog(PerformanceHealthLevel severity, string category, string message)
    {
        lock (_lock)
        {
            if (_serviceLogs.Count >= 500)
            {
                _serviceLogs.RemoveAt(0);
            }
            _serviceLogs.Add(new DiagnosticLogEntry(DateTime.Now, severity, category, message));
        }
    }

    public IReadOnlyList<DiagnosticLogEntry> GetRecentLogs()
    {
        lock (_lock)
        {
            return _diagnosticLogs.ToArray();
        }
    }

    public IReadOnlyList<DiagnosticLogEntry> GetRecentServiceLogs()
    {
        lock (_lock)
        {
            return _serviceLogs.ToArray();
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

    public void ClearServiceLogs()
    {
        lock (_lock)
        {
            _serviceLogs.Clear();
            AddServiceLog(PerformanceHealthLevel.Nominal, "INFO", "Service logs cleared.");
        }
    }

    private readonly struct AnomalyMetrics
    {
        public double Fps { get; init; }
        public int Hz { get; init; }
        public double FrameTimeMs { get; init; }
        public double DispatcherLatencyMs { get; init; }
        public double ProcCpu { get; init; }
        public double GlobalCpu { get; init; }
        public int ThreadCount { get; init; }
        public ulong ProcWorkingSet { get; init; }
        public long ManagedHeapBytes { get; init; }
        public int Gen2 { get; init; }
        public double GlobalRamPercent { get; init; }
        public ulong GlobalRamAvail { get; init; }
    }
}
