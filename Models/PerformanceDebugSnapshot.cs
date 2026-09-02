using System;

namespace VNotch.Models;

public enum PerformanceHealthLevel
{
    Nominal,
    Warning,
    Critical
}

public sealed record DiagnosticLogEntry(
    DateTime Timestamp,
    PerformanceHealthLevel Severity,
    string Category,
    string Message);

public sealed class PerformanceDebugSnapshot
{
    public double Fps { get; init; }
    public int RefreshRateHz { get; init; }
    public double FrameTimeMs { get; init; }
    public double DispatcherLatencyMs { get; init; }
    public string GpuName { get; init; } = "DirectX Display Adapter";
    public ulong DedicatedVramBytes { get; init; }

    // V-Notch CPU & Threads
    public double ProcessCpuPercent { get; init; }
    public int ProcessThreadCount { get; init; }
    public int ProcessHandleCount { get; init; }

    // V-Notch RAM & GC
    public ulong ProcessWorkingSetBytes { get; init; }
    public ulong ProcessPrivateBytes { get; init; }
    public long ManagedHeapBytes { get; init; }
    public double AllocBytesPerSec { get; init; }
    public int GcGen0Count { get; init; }
    public int GcGen1Count { get; init; }
    public int GcGen2Count { get; init; }

    // V-Notch GPU
    public double ProcessGpuPercent { get; init; }

    // Global System CPU
    public double GlobalCpuPercent { get; init; }

    // Global System RAM
    public ulong GlobalRamUsedBytes { get; init; }
    public ulong GlobalRamTotalBytes { get; init; }
    public ulong GlobalRamAvailBytes { get; init; }
    public double GlobalRamPercent { get; init; }

    // Global System GPU
    public double GlobalGpuPercent { get; init; }

    // Global Network
    public double NetDownBytesPerSec { get; init; }
    public double NetUpBytesPerSec { get; init; }

    // Health & Diagnostics
    public PerformanceHealthLevel HealthLevel { get; init; } = PerformanceHealthLevel.Nominal;
    public string HealthStatusSummary { get; init; } = "Performance Nominal";
}

