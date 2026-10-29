using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using VNotch.Models;
using Vortice.DXGI;

namespace VNotch.Services;

public sealed partial class GpuMonitorService : IDisposable, IAsyncDisposable
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_gpu_monitor_score_adapter(
        ReadOnlySpan<char> name,
        int nameLen,
        ulong vramBytes,
        out long outScore);

    [LibraryImport(NativeDll)]
    private static partial int vn_gpu_monitor_compute_process_cpu_delta(
        ulong procTotalTime,
        ulong lastProcTime,
        long nowTicks,
        long lastProcTicks,
        long stopwatchFreq,
        int processorCount,
        double smoothedProcCpu,
        out double outCpu);

    [LibraryImport(NativeDll)]
    private static partial int vn_gpu_monitor_compute_global_cpu_delta(
        ulong idle,
        ulong lastSysIdle,
        ulong kernel,
        ulong lastSysKernel,
        ulong user,
        ulong lastSysUser,
        double smoothedGlobalCpu,
        out double outCpu);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_gpu_monitor_is_target_engine_instance(
        ReadOnlySpan<char> inst,
        int instLen,
        out byte outIsTarget);

    private static readonly Lazy<GpuMonitorService> SharedInstance = new(() => new GpuMonitorService());
    public static GpuMonitorService Instance => SharedInstance.Value;
    internal static ValueTask DisposeSharedAsync() => SharedInstance.IsValueCreated ? SharedInstance.Value.DisposeAsync() : ValueTask.CompletedTask;
    private volatile bool _disposed;

    private string? _gpuName;
    private ulong _dedicatedVramBytes;
    private bool _gpuInfoInitialized = false;
    private readonly object _gpuInfoLock = new();

    private readonly IntPtr _currentProcessHandle = Win32Interop.GetCurrentProcess();
    private readonly int _currentPid = Environment.ProcessId;
    private readonly int _processorCount = Math.Max(1, Environment.ProcessorCount);

    // Process CPU tracking (GetProcessTimes)
    private ulong _lastProcTime = 0;
    private long _lastProcTicks = 0;

    // System CPU tracking (GetSystemTimes)
    private ulong _lastSysIdle = 0;
    private ulong _lastSysKernel = 0;
    private ulong _lastSysUser = 0;

    // Smooth CPU values to prevent discrete thread quantum jitter
    private double _smoothedProcCpu = 0;
    private double _smoothedGlobalCpu = 0;

    // Cached GPU Usage (Updated asynchronously by background worker)
    private volatile float _cachedProcessGpu = 0;
    private volatile float _cachedGlobalGpu = 0;

    private Thread? _gpuSamplerThread;
    private volatile bool _isRunning = false;
    private int _consumerCount = 0;
    private readonly AutoResetEvent _wakeSamplerEvent = new(false);
    private readonly object _samplerLock = new();

    public bool IsRunning => _isRunning;

    public int ConsumerCount
    {
        get
        {
            lock (_samplerLock)
            {
                return _consumerCount;
            }
        }
    }

    public GpuMonitorService()
    {
    }

    public void Start()
    {
        lock (_samplerLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _consumerCount++;
            if (_consumerCount == 1 && !_isRunning)
            {
                _isRunning = true;
                if (_gpuSamplerThread == null)
                {
                    _gpuSamplerThread = new Thread(GpuSamplingWorker)
                    {
                        IsBackground = true,
                        Name = "VNotch-GpuPerformanceWorker",
                        Priority = ThreadPriority.Lowest
                    };
                    _gpuSamplerThread.Start();
                }
                _wakeSamplerEvent.Set();
            }
        }
    }

    public void Stop()
    {
        lock (_samplerLock)
        {
            if (_disposed) return;
            if (_consumerCount > 0)
                _consumerCount--;

            if (_consumerCount == 0 && _isRunning)
            {
                _isRunning = false;
                _wakeSamplerEvent.Set();
            }
        }
    }

    public void EnsureSamplerRunning()
    {
        lock (_samplerLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_consumerCount == 0)
                _consumerCount = 1;

            if (!_isRunning)
            {
                _isRunning = true;
                if (_gpuSamplerThread == null)
                {
                    _gpuSamplerThread = new Thread(GpuSamplingWorker)
                    {
                        IsBackground = true,
                        Name = "VNotch-GpuPerformanceWorker",
                        Priority = ThreadPriority.Lowest
                    };
                    _gpuSamplerThread.Start();
                }
                _wakeSamplerEvent.Set();
            }
        }
    }

    public (float ProcessGpuPercent, float GlobalGpuPercent) GetGpuUsage()
    {
        return (_cachedProcessGpu, _cachedGlobalGpu);
    }

    public (string GpuName, ulong DedicatedVramBytes) GetGpuInfo()
    {
        if (_gpuInfoInitialized && _gpuName != null)
        {
            return (_gpuName, _dedicatedVramBytes);
        }

        lock (_gpuInfoLock)
        {
            if (_gpuInfoInitialized && _gpuName != null)
            {
                return (_gpuName, _dedicatedVramBytes);
            }

            try
            {
                using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
                string? bestGpuName = null;
                ulong bestVram = 0;
                long bestScore = -1;

                for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
                {
                    using (adapter)
                    {
                        var desc = adapter.Description1;
                        if ((desc.Flags & AdapterFlags.Software) != 0) continue;

                        string name = desc.Description.Trim();
                        ulong vram = (ulong)desc.DedicatedVideoMemory;

                        // Score GPUs by dedicated VRAM (MB) with discrete high-performance bonuses.
                        _ = vn_gpu_monitor_score_adapter(name.AsSpan(), name.Length, vram, out long score);

                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestGpuName = name;
                            bestVram = vram;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(bestGpuName))
                {
                    _gpuName = bestGpuName;
                    _dedicatedVramBytes = bestVram;
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Log("GPU-MONITOR", $"Failed to query DXGI adapter: {ex.Message}");
                _gpuName = "DirectX Display Adapter";
            }

            _gpuInfoInitialized = true;
            return (_gpuName ?? "GPU", _dedicatedVramBytes);
        }
    }

    public PerformanceDebugSnapshot SampleFastMetrics(double fps, int hz, double netDown = 0, double netUp = 0)
    {
        long nowTicks = Stopwatch.GetTimestamp();
        double procCpu = SampleProcessCpu(nowTicks);
        ulong procRam = SampleProcessRam();
        double globalCpu = SampleGlobalCpu();
        var (globalRamUsed, globalRamTotal, globalRamPercent) = SampleGlobalRam();
        var (gpuName, vram) = GetGpuInfo();

        return new PerformanceDebugSnapshot
        {
            Fps = fps,
            RefreshRateHz = hz,
            GpuName = gpuName,
            DedicatedVramBytes = vram,
            ProcessCpuPercent = procCpu,
            GlobalCpuPercent = globalCpu,
            ProcessWorkingSetBytes = procRam,
            GlobalRamUsedBytes = globalRamUsed,
            GlobalRamTotalBytes = globalRamTotal,
            GlobalRamPercent = globalRamPercent,
            ProcessGpuPercent = _cachedProcessGpu,
            GlobalGpuPercent = _cachedGlobalGpu,
            NetDownBytesPerSec = netDown,
            NetUpBytesPerSec = netUp
        };
    }

    private double SampleProcessCpu(long nowTicks)
    {
        try
        {
            if (!Win32Interop.GetProcessTimes(_currentProcessHandle, out _, out _, out var procKernel, out var procUser))
            {
                return 0.0;
            }

            ulong procTotalTime = procKernel.ToUInt64() + procUser.ToUInt64();
            _smoothedProcCpu = ComputeProcessCpuDelta(procTotalTime, nowTicks);

            _lastProcTime = procTotalTime;
            _lastProcTicks = nowTicks;
            return _smoothedProcCpu < 0.05 ? 0.0 : _smoothedProcCpu;
        }
        catch (Exception)
        {
            // Process times query failed
        }
        return 0.0;
    }

    private double ComputeProcessCpuDelta(ulong procTotalTime, long nowTicks)
    {
        _ = vn_gpu_monitor_compute_process_cpu_delta(
            procTotalTime,
            _lastProcTime,
            nowTicks,
            _lastProcTicks,
            Stopwatch.Frequency,
            _processorCount,
            _smoothedProcCpu,
            out double cpu);
        return cpu;
    }

    private ulong SampleProcessRam()
    {
        try
        {
            uint cb = (uint)Marshal.SizeOf<Win32Interop.PROCESS_MEMORY_COUNTERS_EX>();
            if (Win32Interop.GetProcessMemoryInfo(_currentProcessHandle, out var memCounters, cb))
            {
                return memCounters.WorkingSetSize;
            }
        }
        catch (Exception)
        {
            // Process memory query failed
        }
        return 0;
    }

    private double SampleGlobalCpu()
    {
        try
        {
            if (!Win32Interop.GetSystemTimes(out var sysIdle, out var sysKernel, out var sysUser))
            {
                return 0.0;
            }

            ulong idle = sysIdle.ToUInt64();
            ulong kernel = sysKernel.ToUInt64();
            ulong user = sysUser.ToUInt64();

            _smoothedGlobalCpu = ComputeGlobalCpuDelta(idle, kernel, user);

            _lastSysIdle = idle;
            _lastSysKernel = kernel;
            _lastSysUser = user;
            return _smoothedGlobalCpu;
        }
        catch (Exception)
        {
            // System times query failed
        }
        return 0.0;
    }

    private double ComputeGlobalCpuDelta(ulong idle, ulong kernel, ulong user)
    {
        _ = vn_gpu_monitor_compute_global_cpu_delta(
            idle,
            _lastSysIdle,
            kernel,
            _lastSysKernel,
            user,
            _lastSysUser,
            _smoothedGlobalCpu,
            out double cpu);
        return cpu;
    }

    private static (ulong UsedBytes, ulong TotalBytes, double Percent) SampleGlobalRam()
    {
        try
        {
            var memStatus = new Win32Interop.MEMORYSTATUSEX_METRICS
            {
                dwLength = (uint)Marshal.SizeOf<Win32Interop.MEMORYSTATUSEX_METRICS>()
            };
            if (Win32Interop.GlobalMemoryStatusEx(ref memStatus))
            {
                ulong total = memStatus.ullTotalPhys;
                ulong used = total > memStatus.ullAvailPhys ? total - memStatus.ullAvailPhys : 0;
                double percent = Math.Clamp(memStatus.dwMemoryLoad, 0, 100);
                return (used, total, percent);
            }
        }
        catch (Exception)
        {
            // Memory status query failed
        }
        return (0, 0, 0);
    }

    private readonly struct GpuCounterItem
    {
        public readonly PerformanceCounter Counter;
        public readonly bool IsCurrentProcess;

        public GpuCounterItem(PerformanceCounter counter, bool isCurrentProcess)
        {
            Counter = counter;
            IsCurrentProcess = isCurrentProcess;
        }
    }

    private void GpuSamplingWorker()
    {
        List<GpuCounterItem>? counters = null;
        long lastRefresh = 0;
        string pidPrefix = $"pid_{_currentPid}_";

        try
        {
            while (!_disposed)
            {
                if (!_isRunning)
                {
                    DisposeCounterList(counters);
                    counters = null;
                    _wakeSamplerEvent.WaitOne();
                    continue;
                }
                try
                {
                    long now = Stopwatch.GetTimestamp();
                    double secSinceRefresh = (double)(now - lastRefresh) / Stopwatch.Frequency;

                    // Refresh GPU Engine instance counters every 60 seconds (or on first run)
                    if (counters == null || secSinceRefresh > 60.0)
                    {
                        RefreshGpuCounters(ref counters, pidPrefix);
                        lastRefresh = now;
                    }

                    double procTotal = 0;
                    double globalTotal = 0;
                    if (counters != null)
                    {
                        foreach (var item in counters)
                        {
                            try
                            {
                                double val = item.Counter.NextValue();
                                globalTotal += val;
                                if (item.IsCurrentProcess)
                                {
                                    procTotal += val;
                                }
                            }
                            catch
                            {
                                // Counter reading failed
                            }
                        }
                    }

                    _cachedProcessGpu = (float)Math.Clamp(procTotal, 0, 100);
                    _cachedGlobalGpu = (float)Math.Clamp(globalTotal, 0, 100);
                }
                catch (Exception)
                {
                    // GPU sampling cycle error
                }

                _wakeSamplerEvent.WaitOne(1000);
            }
        }
        finally
        {
            DisposeCounterList(counters);
            lock (_samplerLock) _wakeSamplerEvent.Dispose();
        }
    }

    private static void RefreshGpuCounters(ref List<GpuCounterItem>? counters, string pidPrefix)
    {
        DisposeCounterList(counters);
        counters = new List<GpuCounterItem>();

        try
        {
            var cat = new PerformanceCounterCategory("GPU Engine");
            var insts = cat.GetInstanceNames();
            var targetInstances = insts.Where(inst =>
                inst != null &&
                vn_gpu_monitor_is_target_engine_instance(inst.AsSpan(), inst.Length, out byte isTarget) == 0 &&
                isTarget != 0);

            const int maxOtherCounters = 32;
            int otherCount = 0;

            foreach (var inst in targetInstances)
            {
                bool isCurrentProcess = inst.StartsWith(pidPrefix, StringComparison.OrdinalIgnoreCase);
                if (!isCurrentProcess)
                {
                    if (otherCount >= maxOtherCounters)
                    {
                        continue;
                    }
                    otherCount++;
                }

                try
                {
                    var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, true);
                    try { counter.NextValue(); counters.Add(new GpuCounterItem(counter, isCurrentProcess)); } catch { counter.Dispose(); throw; }
                    // Sampled inside inner try above.
                    // Added inside inner try above.
                }
                catch (Exception)
                {
                    // Performance counter instance creation failed
                }
            }
        }
        catch (Exception)
        {
            // Performance counter category query failed
        }
    }

    private static void DisposeCounterList(List<GpuCounterItem>? list)
    {
        if (list == null) return;
        foreach (var c in list)
        {
            try
            {
                c.Counter.Dispose();
            }
            catch (Exception)
            {
                // Best effort counter disposal
            }
        }
        list.Clear();
    }

    public void Dispose()
    {
        lock (_samplerLock)
        {
            if (_disposed) return;
            _disposed = true;
            _isRunning = false;
            _consumerCount = 0;
            _wakeSamplerEvent.Set();
            // The worker owns the handle until its final WaitOne has returned.
            if (_gpuSamplerThread == null) _wakeSamplerEvent.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        Thread? worker;
        lock (_samplerLock) worker = _gpuSamplerThread;
        if (worker != null) await System.Threading.Tasks.Task.Run(() => worker.Join()).ConfigureAwait(false);
    }
}

