using System;
using System.Runtime.InteropServices;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.Modules;

/// <summary>
/// Tick scheduler for the system monitor. CPU, RAM and network sampling (including the smoothing
/// and delta state) run in VNotch.Native; this class only forwards the sampled numbers.
/// </summary>
public sealed partial class SystemMonitorModule : NotchModuleBase
{
    private const string NativeDll = "VNotch.Native.dll";

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnSystemMonitorState
    {
        public ulong LastIdle;
        public ulong LastKernel;
        public ulong LastUser;
        public long LastCpuQpc;
        public double SmoothedCpu;
        public byte HasCpuMeasurement;
        public ulong LastRecvBytes;
        public ulong LastSentBytes;
        public long LastNetQpc;
        public double DownBytesPerSec;
        public double UpBytesPerSec;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnSystemMemory
    {
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public uint MemoryLoadPercent;
        public ulong InstalledPhysical;
    }

    [LibraryImport(NativeDll, EntryPoint = "vn_sysmon_reset")]
    private static partial int NativeReset(ref VnSystemMonitorState state);

    [LibraryImport(NativeDll, EntryPoint = "vn_sysmon_sample_cpu")]
    private static partial int NativeSampleCpu(ref VnSystemMonitorState state, out double cpuPercent);

    [LibraryImport(NativeDll, EntryPoint = "vn_sysmon_sample_network")]
    private static partial int NativeSampleNetwork(ref VnSystemMonitorState state, out double downBytesPerSec, out double upBytesPerSec);

    [LibraryImport(NativeDll, EntryPoint = "vn_sysmon_read_memory")]
    private static partial int NativeReadMemory(int includeInstalled, out VnSystemMemory memory);

    public override string ModuleName => "SystemMonitor";

    public override TimeSpan? TickInterval => TimeSpan.FromSeconds(1);

    private VnSystemMonitorState _cpuState;
    private VnSystemMonitorState _netState;

    private int _isNetworkSampling = 0;
    private double _latestDownBytesPerSec = 0;
    private double _latestUpBytesPerSec = 0;
    private readonly object _netStatsLock = new();
    private int _networkGeneration;
    private int _sampledNetworkGeneration = -1;

    private ulong _usablePhysicalBytes;
    private ulong _installedPhysicalBytes;

    public event EventHandler<SystemMonitorInfo>? StatsUpdated;

    protected override void OnInitialize()
    {
        if (NativeReadMemory(1, out var memory) == 0)
        {
            _usablePhysicalBytes = memory.TotalPhysical;
            _installedPhysicalBytes = memory.InstalledPhysical;
        }
        if (_installedPhysicalBytes == 0) _installedPhysicalBytes = _usablePhysicalBytes;
    }

    protected override void OnStart()
    {
        // A hidden widget has no useful sampling interval. Start fresh instead
        // of reporting CPU/network averages over the time it was closed.
        NativeReset(ref _cpuState);
        SampleCpuUsage();
        lock (_netStatsLock)
        {
            _networkGeneration++;
            _latestDownBytesPerSec = 0;
            _latestUpBytesPerSec = 0;
        }
    }

    protected override void OnTick()
    {
        double cpu = SampleCpuUsage();

        ulong used = 0;
        double ramPercent = 0;
        if (NativeReadMemory(0, out var memory) == 0)
        {
            ulong usable = memory.TotalPhysical > 0 ? memory.TotalPhysical : _usablePhysicalBytes;
            ulong available = memory.AvailablePhysical;
            used = available >= usable ? usable : usable - available;
            ramPercent = memory.MemoryLoadPercent;
        }

        if (Interlocked.CompareExchange(ref _isNetworkSampling, 1, 0) == 0)
        {
            Task.Run(SampleNetworkUsageBackground);
        }

        double netDown, netUp;
        lock (_netStatsLock)
        {
            netDown = _latestDownBytesPerSec;
            netUp = _latestUpBytesPerSec;
        }

        StatsUpdated?.Invoke(this, new SystemMonitorInfo
        {
            CpuPercent = cpu,
            RamUsedBytes = used,
            RamTotalBytes = _installedPhysicalBytes > 0 ? _installedPhysicalBytes : _usablePhysicalBytes,
            RamPercent = ramPercent,
            NetDownBytesPerSec = netDown,
            NetUpBytesPerSec = netUp
        });
    }

    private double SampleCpuUsage() =>
        NativeSampleCpu(ref _cpuState, out double cpu) == 0 ? cpu : 0;

    private void SampleNetworkUsageBackground()
    {
        int generation;
        lock (_netStatsLock) generation = _networkGeneration;
        try
        {
            // Only this serialized worker accesses _netState. A restart can
            // invalidate a sample in flight without waiting on native I/O in WPF.
            if (_sampledNetworkGeneration != generation)
            {
                NativeReset(ref _netState);
                _sampledNetworkGeneration = generation;
            }
            int status = NativeSampleNetwork(ref _netState, out double down, out double up);
            lock (_netStatsLock)
            {
                if (generation == _networkGeneration)
                {
                    _latestDownBytesPerSec = down;
                    _latestUpBytesPerSec = up;
                }
            }
            if (status != 0) RuntimeLog.Log("SYSTEM-MONITOR-NET", $"native network sampling failed ({status})");
        }
        catch (Exception ex)
        {
            lock (_netStatsLock)
            {
                if (generation == _networkGeneration)
                {
                    _latestDownBytesPerSec = 0;
                    _latestUpBytesPerSec = 0;
                }
            }
            RuntimeLog.Log("SYSTEM-MONITOR-NET", ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _isNetworkSampling, 0);
        }
    }
}
