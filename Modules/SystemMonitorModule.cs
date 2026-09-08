using System;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.Modules;

public sealed class SystemMonitorModule : NotchModuleBase
{
    public override string ModuleName => "SystemMonitor";

    public override TimeSpan? TickInterval => TimeSpan.FromSeconds(1);

    // CPU Tracking via GetSystemTimes (0 allocations, < 0.001ms)
    private ulong _lastSysIdle = 0;
    private ulong _lastSysKernel = 0;
    private ulong _lastSysUser = 0;
    private double _smoothedCpu = 0;

    // Network Tracking via NetworkInterface IPStatistics
    private long _lastNetRecvBytes = 0;
    private long _lastNetSentBytes = 0;
    private long _lastNetTicks = 0;

    private int _isNetworkSampling = 0;
    private double _latestDownBytesPerSec = 0;
    private double _latestUpBytesPerSec = 0;
    private NetworkInterface[]? _cachedInterfaces;
    private readonly object _interfaceLock = new();

    private ulong _usablePhysicalBytes;
    private ulong _installedPhysicalBytes;

    public event EventHandler<SystemMonitorInfo>? StatsUpdated;

    protected override void OnInitialize()
    {
        try
        {
            NetworkChange.NetworkAddressChanged += OnNetworkTopologyChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkTopologyChanged;
        }
        catch { }

        _usablePhysicalBytes = ReadUsablePhysicalMemory();
        _installedPhysicalBytes = ReadInstalledPhysicalMemory();
        if (_installedPhysicalBytes == 0) _installedPhysicalBytes = _usablePhysicalBytes;

        // Initialize baselines
        SampleCpuUsage();
        if (Interlocked.CompareExchange(ref _isNetworkSampling, 1, 0) == 0)
        {
            Task.Run(SampleNetworkUsageBackground);
        }
    }

    private void OnNetworkTopologyChanged(object? sender, EventArgs e)
    {
        InvalidateNetworkInterfaces();
    }

    private void InvalidateNetworkInterfaces()
    {
        lock (_interfaceLock)
        {
            _cachedInterfaces = null;
        }
    }

    private NetworkInterface[] GetOrRefreshNetworkInterfaces()
    {
        lock (_interfaceLock)
        {
            if (_cachedInterfaces == null)
            {
                try
                {
                    _cachedInterfaces = NetworkInterface.GetAllNetworkInterfaces();
                }
                catch
                {
                    _cachedInterfaces = Array.Empty<NetworkInterface>();
                }
            }
            return _cachedInterfaces;
        }
    }

    protected override void OnTick()
    {
        double cpu = SampleCpuUsage();

        ulong used = 0;
        double ramPercent = 0;
        var memStatus = new Win32Interop.MEMORYSTATUSEX_METRICS();
        memStatus.dwLength = (uint)Marshal.SizeOf<Win32Interop.MEMORYSTATUSEX_METRICS>();
        if (Win32Interop.GlobalMemoryStatusEx(ref memStatus))
        {
            ulong usable = memStatus.ullTotalPhys > 0 ? memStatus.ullTotalPhys : _usablePhysicalBytes;
            ulong available = memStatus.ullAvailPhys;
            used = available >= usable ? usable : usable - available;
            ramPercent = Math.Clamp(memStatus.dwMemoryLoad, 0, 100);
        }

        if (Interlocked.CompareExchange(ref _isNetworkSampling, 1, 0) == 0)
        {
            Task.Run(SampleNetworkUsageBackground);
        }

        StatsUpdated?.Invoke(this, new SystemMonitorInfo
        {
            CpuPercent = cpu,
            RamUsedBytes = used,
            RamTotalBytes = _installedPhysicalBytes > 0 ? _installedPhysicalBytes : _usablePhysicalBytes,
            RamPercent = ramPercent,
            NetDownBytesPerSec = _latestDownBytesPerSec,
            NetUpBytesPerSec = _latestUpBytesPerSec
        });
    }

    private double SampleCpuUsage()
    {
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
                        double rawCpu = Math.Clamp((busy / deltaTotal) * 100.0, 0, 100);
                        _smoothedCpu = _lastSysIdle == 0 ? rawCpu : (_smoothedCpu * 0.85 + rawCpu * 0.15);
                    }
                }

                _lastSysIdle = idle;
                _lastSysKernel = kernel;
                _lastSysUser = user;
                return Math.Clamp(_smoothedCpu, 0, 100);
            }
        }
        catch { }

        return Math.Clamp(_smoothedCpu, 0, 100);
    }

    private void SampleNetworkUsageBackground()
    {
        try
        {
            long nowTicks = Stopwatch.GetTimestamp();
            long totalRecv = 0;
            long totalSent = 0;

            var interfaces = GetOrRefreshNetworkInterfaces();
            foreach (var nic in interfaces)
            {
                try
                {
                    if (nic.OperationalStatus != OperationalStatus.Up ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                        continue;

                    var stats = nic.GetIPStatistics();
                    totalRecv += stats.BytesReceived;
                    totalSent += stats.BytesSent;
                }
                catch
                {
                    // Interface might have gone down during enumeration
                }
            }

            if (_lastNetTicks > 0)
            {
                double elapsedSec = (double)(nowTicks - _lastNetTicks) / Stopwatch.Frequency;
                if (elapsedSec > 0.1 && _lastNetRecvBytes > 0)
                {
                    long deltaRecv = totalRecv > _lastNetRecvBytes ? totalRecv - _lastNetRecvBytes : 0;
                    long deltaSent = totalSent > _lastNetSentBytes ? totalSent - _lastNetSentBytes : 0;
                    _latestDownBytesPerSec = deltaRecv / elapsedSec;
                    _latestUpBytesPerSec = deltaSent / elapsedSec;
                }
            }

            _lastNetRecvBytes = totalRecv;
            _lastNetSentBytes = totalSent;
            _lastNetTicks = nowTicks;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("SYSTEM-MONITOR-NET", ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _isNetworkSampling, 0);
        }
    }

    protected override void OnDispose()
    {
        try
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkTopologyChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkTopologyChanged;
        }
        catch { }
        InvalidateNetworkInterfaces();
    }

    #region Physical memory

    private static ulong ReadUsablePhysicalMemory()
    {
        var memStatus = new Win32Interop.MEMORYSTATUSEX_METRICS();
        memStatus.dwLength = (uint)Marshal.SizeOf<Win32Interop.MEMORYSTATUSEX_METRICS>();
        if (Win32Interop.GlobalMemoryStatusEx(ref memStatus))
        {
            return memStatus.ullTotalPhys;
        }
        return 0;
    }

    private static ulong ReadInstalledPhysicalMemory()
    {
        try
        {
            if (GetPhysicallyInstalledSystemMemory(out ulong totalKb) && totalKb > 0)
                return totalKb * 1024UL;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("MODULE-SystemMonitor", $"GetPhysicallyInstalledSystemMemory failed: {ex.Message}");
        }
        return 0;
    }

    [return: MarshalAs(UnmanagedType.Bool)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    #endregion
}

