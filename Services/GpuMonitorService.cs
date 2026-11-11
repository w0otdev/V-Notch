using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Models;

namespace VNotch.Services;

public sealed partial class GpuMonitorService : IDisposable, IAsyncDisposable
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_gpu_monitor_find_best_adapter(
        Span<char> outName,
        int outNameCap,
        out int outNameLen,
        out ulong outDedicatedVram);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnSystemMetricsState
    {
        public ulong ProcessTime, IdleTime, KernelTime, UserTime;
        public long Timestamp;
        public double ProcessCpu, GlobalCpu;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnSystemMetricsSnapshot
    {
        public double ProcessCpu, GlobalCpu;
        public ulong WorkingSet, RamUsed, RamTotal;
        public double RamPercent, ProcessGpu, GlobalGpu;
    }

    [LibraryImport(NativeDll)]
    private static partial int vn_system_metrics_sample(ref VnSystemMetricsState state, out VnSystemMetricsSnapshot snapshot);
    [LibraryImport(NativeDll)]
    private static partial int vn_gpu_monitor_sample(out VnSystemMetricsSnapshot snapshot);
    [LibraryImport(NativeDll)]
    private static partial int vn_gpu_monitor_reset();

    private static readonly Lazy<GpuMonitorService> SharedInstance = new(() => new GpuMonitorService());
    public static GpuMonitorService Instance => SharedInstance.Value;
    internal static ValueTask DisposeSharedAsync() => SharedInstance.IsValueCreated ? SharedInstance.Value.DisposeAsync() : ValueTask.CompletedTask;
    private volatile bool _disposed;

    private string? _gpuName;
    private ulong _dedicatedVramBytes;
    private bool _gpuInfoInitialized = false;
    private readonly object _gpuInfoLock = new();
    private readonly object _fastMetricsLock = new();

    private VnSystemMetricsState _metricsState;

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

            Span<char> nameBuf = stackalloc char[128];
            int rc = vn_gpu_monitor_find_best_adapter(nameBuf, nameBuf.Length, out int nameLen, out ulong vram);
            if (rc == 0)
            {
                if (nameLen > 0)
                {
                    _gpuName = new string(nameBuf[..nameLen]);
                    _dedicatedVramBytes = vram;
                }
            }
            else
            {
                RuntimeLog.Log("GPU-MONITOR", $"Failed to query DXGI adapter: status {rc}");
                _gpuName = "DirectX Display Adapter";
            }

            _gpuInfoInitialized = true;
            return (_gpuName ?? "GPU", _dedicatedVramBytes);
        }
    }

    public Task<PerformanceDebugSnapshot> GetMetricsAsync(double fps = 0, int hz = 0, double netDown = 0, double netUp = 0)
    {
        return Task.FromResult(SampleFastMetrics(fps, hz, netDown, netUp));
    }

    public PerformanceDebugSnapshot SampleFastMetrics(double fps = 0, int hz = 0, double netDown = 0, double netUp = 0)
    {
        VnSystemMetricsSnapshot metrics;
        lock (_fastMetricsLock)
            vn_system_metrics_sample(ref _metricsState, out metrics);

        var (gpuName, vram) = GetGpuInfo();

        return new PerformanceDebugSnapshot
        {
            Fps = fps,
            RefreshRateHz = hz,
            GpuName = gpuName,
            DedicatedVramBytes = vram,
            ProcessCpuPercent = metrics.ProcessCpu,
            GlobalCpuPercent = metrics.GlobalCpu,
            ProcessWorkingSetBytes = metrics.WorkingSet,
            GlobalRamUsedBytes = metrics.RamUsed,
            GlobalRamTotalBytes = metrics.RamTotal,
            GlobalRamPercent = metrics.RamPercent,
            ProcessGpuPercent = _cachedProcessGpu,
            GlobalGpuPercent = _cachedGlobalGpu,
            NetDownBytesPerSec = netDown,
            NetUpBytesPerSec = netUp
        };
    }

    private void GpuSamplingWorker()
    {
        try
        {
            while (!_disposed)
            {
                if (!_isRunning)
                {
                    vn_gpu_monitor_reset();
                    _cachedProcessGpu = _cachedGlobalGpu = 0;
                    _wakeSamplerEvent.WaitOne();
                    continue;
                }
                vn_gpu_monitor_sample(out var metrics);
                _cachedProcessGpu = (float)metrics.ProcessGpu;
                _cachedGlobalGpu = (float)metrics.GlobalGpu;
                _wakeSamplerEvent.WaitOne(1000);
            }
        }
        finally
        {
            vn_gpu_monitor_reset();
            lock (_samplerLock) _wakeSamplerEvent.Dispose();
        }
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
        if (worker != null) await Task.Run(() => worker.Join()).ConfigureAwait(false);
    }
}
